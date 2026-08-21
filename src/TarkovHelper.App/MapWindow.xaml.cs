using System.IO;
using System.Net.Http;
using System.Windows;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using Svg.Skia;
using TarkovHelper.Core.JsonFallback;
using TarkovHelper.Core.Maps;
using TarkovHelper.Core.Models;
using TarkovHelper.Core.Position;

namespace TarkovHelper.App;

public partial class MapWindow : Window
{
    private readonly MapDataRepository _mapDataRepository;
    private readonly JsonTarkovDevClient _extractsClient = new();
    private readonly HttpClient _http = new();
    private Dictionary<string, MapCalibration>? _calibrations;
    private Dictionary<string, List<MapExtract>>? _extractsByMap;
    private Dictionary<string, List<LootContainerSpawn>>? _lootContainersByMap;

    private MapCalibration? _currentCalibration;
    private List<MapExtract> _currentExtracts = new();
    private List<ObjectiveMarker> _currentObjectiveMarkers = new();
    private List<LootContainerSpawn> _currentLootContainers = new();

    // Once the user manually picks a map from the dropdown, live raid-map
    // updates (SetCurrentMapAsync calls driven by the game's own log/
    // position data) must stop silently overriding that choice - otherwise
    // picking e.g. Woods to browse loot/quest data while sitting in a
    // Customs raid would get reverted on the very next screenshot or log
    // event. Cleared when the user picks "Follow current raid" again.
    private bool _isManualMapOverride;

    // Raised when the user picks a map from the dropdown (including
    // switching back to "follow raid"), so MainWindow can recompute
    // objective markers for whichever map is now actually showing - marker
    // filtering lives in MainWindow (it owns the quest list), not here.
    public event EventHandler<string?>? MapSelectionChanged;

    // Lets MainWindow decide whether a live raid-map change should also
    // move its own "displayed map" bookkeeping (used for objective marker
    // filtering) - false while the user has the dropdown pinned to a
    // specific map, since SetCurrentMapAsync's auto-calls are being ignored
    // in that state anyway (see _isManualMapOverride).
    public bool IsFollowingRaid => !_isManualMapOverride;

    private sealed record MapOption(string Label, string? NormalizedName);

    // SKSvg owns the native memory backing SKPicture.CullRect/etc - the
    // picture is only valid as long as the SKSvg that loaded it is alive,
    // so both must be kept together and disposed together, not just the
    // picture in isolation (doing that caused an AccessViolationException
    // in SKPicture.get_CullRect on repaint, since svg was disposed at the
    // end of the loading method while OnPaintSurface still referenced its
    // picture on every subsequent frame).
    private SKSvg? _currentMapSvg;
    private SKPicture? _currentMapPicture;
    private PlayerPosition? _lastPosition;

    // Guards against a real race introduced once the map window could be
    // created/updated automatically on every screenshot rather than only
    // via a manual "Open map" click: two SetCurrentMapAsync calls can now
    // land back-to-back for DIFFERENT maps (e.g. one seeded with a stale
    // map from the previous raid at window-creation time, immediately
    // followed by MapLoaded firing for the real new-raid map) - without
    // this, whichever async SVG fetch happens to finish LAST wins, even if
    // it was for the stale, earlier-requested map, which is exactly what a
    // real user hit (map got stuck on the previous raid's map - Streets -
    // and never corrected to the new raid's map - Woods - despite
    // SetCurrentMapAsync("woods") having been called correctly). Each call
    // captures its own token; a call only commits its result if it's still
    // the most recent request when its async work completes.
    private int _mapRequestGeneration;

    public MapWindow(string appDataFolder)
    {
        InitializeComponent();
        _mapDataRepository = new MapDataRepository(null, appDataFolder);
        Loaded += async (_, _) => await LoadCalibrationsAsync();
        Loaded += async (_, _) => await LoadExtractsAsync();
        Loaded += async (_, _) => await LoadLootContainersAsync();
        Closed += (_, _) => _currentMapSvg?.Dispose();
    }

    private async Task LoadCalibrationsAsync()
    {
        try
        {
            _calibrations = await _mapDataRepository.LoadCalibrationsAsync();
            MapStatusText.Text = $"Map data loaded ({_calibrations.Count} maps). Waiting for current map...";
            PopulateMapSelector();
        }
        catch (Exception ex)
        {
            MapStatusText.Text = $"Failed to load map data: {ex.Message}";
        }
    }

    private void PopulateMapSelector()
    {
        if (_calibrations is null)
        {
            return;
        }

        var options = new List<MapOption> { new("Follow current raid", null) };
        options.AddRange(_calibrations.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new MapOption(TitleCaseNormalizedName(name), name)));

        MapSelector.SelectionChanged -= OnMapSelectorChanged;
        MapSelector.ItemsSource = options;
        MapSelector.SelectedIndex = 0;
        MapSelector.SelectionChanged += OnMapSelectorChanged;
    }

    private static string TitleCaseNormalizedName(string normalizedName) =>
        string.Join(' ', normalizedName.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    // Keeps the dropdown's displayed selection truthful after a
    // SetCurrentMapAsync call it didn't itself originate (e.g. MainWindow
    // seeding the initial map, or a live raid-map change while not
    // overridden) - without this the dropdown would silently drift out of
    // sync with what's actually on screen. isUserSelection calls already
    // reflect the user's own click and don't need correcting.
    private void SyncMapSelectorTo(string normalizedName, bool isUserSelection)
    {
        if (isUserSelection || MapSelector.ItemsSource is not IEnumerable<MapOption> options)
        {
            return;
        }

        var targetLabel = _isManualMapOverride ? normalizedName : null;
        var match = options.FirstOrDefault(o => o.NormalizedName == targetLabel);
        if (match is null || ReferenceEquals(MapSelector.SelectedItem, match))
        {
            return;
        }

        MapSelector.SelectionChanged -= OnMapSelectorChanged;
        MapSelector.SelectedItem = match;
        MapSelector.SelectionChanged += OnMapSelectorChanged;
    }

    private void OnMapSelectorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MapSelector.SelectedItem is not MapOption option)
        {
            return;
        }

        if (option.NormalizedName is null)
        {
            // "Follow current raid" - just stop overriding; the map already
            // on screen is whatever was last selected, which may now be
            // stale, so callers (MainWindow) should push the live raid map
            // back in via SetCurrentMapAsync in response to this event.
            _isManualMapOverride = false;
            MapSelectionChanged?.Invoke(this, null);
            return;
        }

        _isManualMapOverride = true;
        _ = SetCurrentMapAsync(option.NormalizedName, isUserSelection: true);
        MapSelectionChanged?.Invoke(this, option.NormalizedName);
    }

    private async Task LoadExtractsAsync()
    {
        try
        {
            _extractsByMap = await _extractsClient.GetExtractsByMapAsync();
            if (_currentCalibration is not null)
            {
                UpdateCurrentExtracts(_currentCalibration.NormalizedName);
                MapCanvas.InvalidateVisual();
            }
        }
        catch (Exception)
        {
            // Extracts are a display enhancement, not core functionality -
            // fail quietly and just show the map without them rather than
            // blocking on this fetch or clobbering MapStatusText's more
            // important calibration/image-load status.
            _extractsByMap = null;
        }
    }

    private void UpdateCurrentExtracts(string normalizedName)
    {
        _currentExtracts = _extractsByMap is not null && _extractsByMap.TryGetValue(normalizedName, out var extracts)
            ? extracts
            : new List<MapExtract>();
    }

    private async Task LoadLootContainersAsync()
    {
        try
        {
            _lootContainersByMap = await _extractsClient.GetLootContainersByMapAsync();
            if (_currentCalibration is not null)
            {
                UpdateCurrentLootContainers(_currentCalibration.NormalizedName);
                MapCanvas.InvalidateVisual();
            }
        }
        catch (Exception)
        {
            // Loot overlay is a display enhancement, not core functionality -
            // fail quietly and just show the map without it, same as extracts.
            _lootContainersByMap = null;
        }
    }

    private void UpdateCurrentLootContainers(string normalizedName)
    {
        _currentLootContainers = _lootContainersByMap is not null && _lootContainersByMap.TryGetValue(normalizedName, out var containers)
            ? containers
            : new List<LootContainerSpawn>();
    }

    private void OnShowLootChanged(object sender, RoutedEventArgs e) => MapCanvas.InvalidateVisual();

    // normalizedName must already be resolved from EFT's internal map ID
    // (e.g. "bigmap") by the caller - this window only knows tarkov.dev
    // names, since that's what the calibration data is keyed by.
    //
    // isUserSelection distinguishes the dropdown's own call (always allowed)
    // from the automatic calls MainWindow makes as the live raid map changes
    // (skipped while the user has manually overridden the dropdown - see
    // _isManualMapOverride - so an in-progress raid's own map updates don't
    // silently yank the view away from a map the user chose to look at).
    public async Task SetCurrentMapAsync(string normalizedName, bool isUserSelection = false)
    {
        if (_isManualMapOverride && !isUserSelection)
        {
            return;
        }

        var requestGeneration = ++_mapRequestGeneration;

        if (_calibrations is null)
        {
            await LoadCalibrationsAsync();
        }

        // A newer SetCurrentMapAsync call arrived while this one was
        // awaiting calibration data - abandon this one rather than risk it
        // completing after (and overwriting) the newer, more correct call.
        if (requestGeneration != _mapRequestGeneration)
        {
            return;
        }

        if (_calibrations is null || !_calibrations.TryGetValue(normalizedName, out var calibration))
        {
            _currentCalibration = null;
            _currentMapPicture = null;
            _currentExtracts = new List<MapExtract>();
            _currentLootContainers = new List<LootContainerSpawn>();
            MapStatusText.Text = $"No map data available for '{normalizedName}'";
            MapCanvas.InvalidateVisual();
            return;
        }

        UpdateCurrentExtracts(normalizedName);
        UpdateCurrentLootContainers(normalizedName);
        SyncMapSelectorTo(normalizedName, isUserSelection);

        _currentCalibration = calibration;
        _currentMapPicture = null;
        var previousSvg = _currentMapSvg;
        _currentMapSvg = null;
        MapStatusText.Text = $"Loading map image for {normalizedName}...";

        if (calibration.SvgPath is not null)
        {
            try
            {
                var svgBytes = await _http.GetByteArrayAsync(calibration.SvgPath);

                // Same check again after the (potentially slow) network
                // fetch: if a newer request has since started, this one's
                // result is stale and must not overwrite it - this is the
                // exact race that got the map stuck on the previous raid's
                // map (e.g. Streets) instead of the new raid's map (e.g.
                // Woods), since nothing previously stopped an
                // earlier-started-but-later-finishing fetch from winning.
                if (requestGeneration != _mapRequestGeneration)
                {
                    return;
                }

                var svg = new SKSvg();
                using var stream = new MemoryStream(svgBytes);
                _currentMapPicture = svg.Load(stream);
                _currentMapSvg = svg;
                MapStatusText.Text = $"Map: {normalizedName}";
            }
            catch (Exception ex)
            {
                if (requestGeneration != _mapRequestGeneration)
                {
                    return;
                }

                MapStatusText.Text = $"Failed to load map image: {ex.Message}";
            }
        }

        // Safe to dispose only now that the new picture (if any) is fully
        // loaded and assigned - the old one is no longer referenced by
        // OnPaintSurface once _currentMapPicture has been reassigned above.
        previousSvg?.Dispose();

        MapCanvas.InvalidateVisual();
    }

    public void UpdatePosition(PlayerPosition position)
    {
        _lastPosition = position;
        MapCanvas.InvalidateVisual();
    }

    // Caller (MainWindow) is responsible for filtering to active/incomplete
    // quests and the current map - this window just renders whatever list
    // it's given.
    public void SetObjectiveMarkers(List<ObjectiveMarker> markers)
    {
        _currentObjectiveMarkers = markers;
        MapCanvas.InvalidateVisual();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => MapCanvas.InvalidateVisual();

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x11, 0x11, 0x11));

        if (_currentMapPicture is null || _currentCalibration is null)
        {
            return;
        }

        var picture = _currentMapPicture;
        var bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // Fit the map image to the canvas, preserving aspect ratio.
        var canvasWidth = e.Info.Width;
        var canvasHeight = e.Info.Height;
        var scale = Math.Min(canvasWidth / bounds.Width, canvasHeight / bounds.Height);
        var offsetX = (canvasWidth - bounds.Width * scale) / 2f;
        var offsetY = (canvasHeight - bounds.Height * scale) / 2f;

        canvas.Save();
        canvas.Translate(offsetX, offsetY);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        canvas.Restore();

        DrawPois(canvas, _currentCalibration, bounds.Width, bounds.Height, scale, offsetX, offsetY);
        if (ShowLootCheckBox.IsChecked == true)
        {
            DrawLootContainers(canvas, _currentLootContainers, _currentCalibration, bounds.Width, bounds.Height, scale, offsetX, offsetY);
        }
        DrawExtracts(canvas, _currentExtracts, _currentCalibration, bounds.Width, bounds.Height, scale, offsetX, offsetY);
        DrawObjectiveMarkers(canvas, _currentObjectiveMarkers, _currentCalibration, bounds.Width, bounds.Height, scale, offsetX, offsetY);

        if (_lastPosition is { } position)
        {
            DrawMarker(canvas, position, _currentCalibration, bounds.Width, bounds.Height, scale, offsetX, offsetY);
        }
    }

    private static void DrawPois(
        SKCanvas canvas, MapCalibration calibration,
        float svgViewBoxWidth, float svgViewBoxHeight, float scale, float offsetX, float offsetY)
    {
        if (calibration.Pois.Count == 0)
        {
            return;
        }

        using var font = new SKFont { Size = 13 };
        using var outlinePaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 220),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
        };
        using var fillPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        foreach (var poi in calibration.Pois)
        {
            (float mapPixelX, float mapPixelY) = MapCoordinateTransform.WorldToSvgPixel(
                poi.X, poi.Z, calibration, svgViewBoxWidth, svgViewBoxHeight);

            var canvasX = mapPixelX * scale + offsetX;
            var canvasY = mapPixelY * scale + offsetY;

            // Outline drawn first (stroke, wider) then fill on top - the
            // standard technique for text readable against any background.
            canvas.DrawText(poi.Text, canvasX, canvasY, SKTextAlign.Center, font, outlinePaint);
            canvas.DrawText(poi.Text, canvasX, canvasY, SKTextAlign.Center, font, fillPaint);
        }
    }

    private static readonly SKColor PmcExtractColor = new(0x00, 0xe5, 0x99);
    private static readonly SKColor ScavExtractColor = new(0xff, 0x78, 0x00);
    private static readonly SKColor SharedExtractColor = new(0x00, 0xe4, 0xe5);

    private static void DrawExtracts(
        SKCanvas canvas, List<MapExtract> extracts, MapCalibration calibration,
        float svgViewBoxWidth, float svgViewBoxHeight, float scale, float offsetX, float offsetY)
    {
        if (extracts.Count == 0)
        {
            return;
        }

        using var dotStrokePaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        using var font = new SKFont { Size = 12 };
        using var textOutlinePaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 220),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
        };
        using var textFillPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        foreach (var extract in extracts)
        {
            (float mapPixelX, float mapPixelY) = MapCoordinateTransform.WorldToSvgPixel(
                extract.X, extract.Z, calibration, svgViewBoxWidth, svgViewBoxHeight);

            var canvasX = mapPixelX * scale + offsetX;
            var canvasY = mapPixelY * scale + offsetY;

            var color = extract.Faction switch
            {
                "pmc" => PmcExtractColor,
                "scav" => ScavExtractColor,
                _ => SharedExtractColor,
            };

            using var dotFillPaint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawCircle(canvasX, canvasY, 4, dotFillPaint);
            canvas.DrawCircle(canvasX, canvasY, 4, dotStrokePaint);

            var textX = canvasX + 6;
            var textY = canvasY + 4;
            canvas.DrawText(extract.Name, textX, textY, SKTextAlign.Left, font, textOutlinePaint);
            canvas.DrawText(extract.Name, textX, textY, SKTextAlign.Left, font, textFillPaint);
        }
    }

    private static readonly SKColor LootTierHighColor = new(0xff, 0x45, 0x45);
    private static readonly SKColor LootTierMediumColor = new(0xff, 0xb3, 0x00);
    private static readonly SKColor LootTierLowColor = new(0x88, 0x88, 0x88);

    // No text labels here, unlike extracts/objectives - a map can have
    // 500+ container spawn points (e.g. Customs), so labeling every one
    // would be unreadable clutter. Tier is conveyed by dot color/size
    // instead; hovering isn't implemented, but the name is available on
    // LootContainerSpawn if a future tooltip needs it.
    private static void DrawLootContainers(
        SKCanvas canvas, List<LootContainerSpawn> containers, MapCalibration calibration,
        float svgViewBoxWidth, float svgViewBoxHeight, float scale, float offsetX, float offsetY)
    {
        if (containers.Count == 0)
        {
            return;
        }

        using var strokePaint = new SKPaint { Color = new SKColor(0, 0, 0, 180), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };

        foreach (var container in containers)
        {
            (float mapPixelX, float mapPixelY) = MapCoordinateTransform.WorldToSvgPixel(
                container.X, container.Z, calibration, svgViewBoxWidth, svgViewBoxHeight);

            var canvasX = mapPixelX * scale + offsetX;
            var canvasY = mapPixelY * scale + offsetY;

            var tier = LootContainerTiers.GetTier(container.ContainerNormalizedName);
            var (color, radius) = tier switch
            {
                LootTier.High => (LootTierHighColor, 4f),
                LootTier.Low => (LootTierLowColor, 2f),
                _ => (LootTierMediumColor, 3f),
            };

            using var fillPaint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawCircle(canvasX, canvasY, radius, fillPaint);
            canvas.DrawCircle(canvasX, canvasY, radius, strokePaint);
        }
    }

    private static readonly SKColor ObjectiveMarkerColor = new(0xff, 0xd7, 0x00);

    private static void DrawObjectiveMarkers(
        SKCanvas canvas, List<ObjectiveMarker> markers, MapCalibration calibration,
        float svgViewBoxWidth, float svgViewBoxHeight, float scale, float offsetX, float offsetY)
    {
        if (markers.Count == 0)
        {
            return;
        }

        const float halfSize = 6f;
        using var diamondFillPaint = new SKPaint { Color = ObjectiveMarkerColor, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var diamondStrokePaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        using var font = new SKFont { Size = 12 };
        using var textOutlinePaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 220),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
        };
        using var textFillPaint = new SKPaint { Color = ObjectiveMarkerColor, IsAntialias = true };

        foreach (var marker in markers)
        {
            (float mapPixelX, float mapPixelY) = MapCoordinateTransform.WorldToSvgPixel(
                marker.X, marker.Z, calibration, svgViewBoxWidth, svgViewBoxHeight);

            var canvasX = mapPixelX * scale + offsetX;
            var canvasY = mapPixelY * scale + offsetY;

            // Diamond shape distinguishes objectives from extract dots at a glance.
            var pathBuilder = new SKPathBuilder();
            pathBuilder.MoveTo(canvasX, canvasY - halfSize);
            pathBuilder.LineTo(canvasX + halfSize, canvasY);
            pathBuilder.LineTo(canvasX, canvasY + halfSize);
            pathBuilder.LineTo(canvasX - halfSize, canvasY);
            pathBuilder.Close();
            using var path = pathBuilder.Detach();

            canvas.DrawPath(path, diamondFillPaint);
            canvas.DrawPath(path, diamondStrokePaint);

            var textX = canvasX + halfSize + 2;
            var textY = canvasY + 4;
            canvas.DrawText(marker.QuestName, textX, textY, SKTextAlign.Left, font, textOutlinePaint);
            canvas.DrawText(marker.QuestName, textX, textY, SKTextAlign.Left, font, textFillPaint);
        }
    }

    private static void DrawMarker(
        SKCanvas canvas, PlayerPosition position, MapCalibration calibration,
        float svgViewBoxWidth, float svgViewBoxHeight, float scale, float offsetX, float offsetY)
    {
        (float mapPixelX, float mapPixelY) = MapCoordinateTransform.WorldToSvgPixel(
            position.X, position.Z, calibration, svgViewBoxWidth, svgViewBoxHeight);

        var canvasX = mapPixelX * scale + offsetX;
        var canvasY = mapPixelY * scale + offsetY;

        using var fillPaint = new SKPaint { Color = SKColors.Red, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var strokePaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };

        canvas.DrawCircle(canvasX, canvasY, 8, fillPaint);
        canvas.DrawCircle(canvasX, canvasY, 8, strokePaint);

        // Direction indicator: a line from the marker pointing along yaw.
        // The raw yaw is in world space, but the marker is drawn in the
        // map's rotated/projected space (calibration.CoordinateRotationDegrees),
        // so the two must be combined - verified against tarkov-dev's own
        // player-position marker logic (src/pages/map/index.jsx): rotation
        // = playerPosition.rotation + mapData.coordinateRotation, with an
        // extra +180 for maps whose coordinateRotation is 90 or 270.
        var mapRotation = calibration.CoordinateRotationDegrees;
        if (mapRotation is 90f or 270f)
        {
            mapRotation += 180f;
        }

        var yawRadians = (position.YawDegrees + mapRotation) * MathF.PI / 180f;
        var directionLength = 20f;
        var dirX = canvasX + MathF.Sin(yawRadians) * directionLength;
        var dirY = canvasY - MathF.Cos(yawRadians) * directionLength;

        using var linePaint = new SKPaint { Color = SKColors.Yellow, IsAntialias = true, StrokeWidth = 3 };
        canvas.DrawLine(canvasX, canvasY, dirX, dirY, linePaint);
    }
}
