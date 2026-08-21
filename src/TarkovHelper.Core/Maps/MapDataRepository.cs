using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovHelper.Core.Maps;

// Fetches and caches tarkov-dev's maps.json (the only source of map
// image/calibration data - the GraphQL API exposes none, see
// MapCalibration's remarks). Each top-level entry groups multiple
// "projection" variants per map; only the "interactive" one carries real
// transform/rotation data used for marker placement.
public class MapDataRepository
{
    private const string MapsJsonUrl = "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json";

    private readonly HttpClient _http;
    private readonly string _cacheFilePath;

    public MapDataRepository(HttpClient? httpClient, string appDataFolder)
    {
        _http = httpClient ?? new HttpClient();
        Directory.CreateDirectory(appDataFolder);
        _cacheFilePath = Path.Combine(appDataFolder, "maps-cache.json");
    }

    public async Task<Dictionary<string, MapCalibration>> LoadCalibrationsAsync(
        bool forceRefresh = false, CancellationToken ct = default)
    {
        string? rawJson = null;

        if (!forceRefresh && File.Exists(_cacheFilePath))
        {
            rawJson = await File.ReadAllTextAsync(_cacheFilePath, ct);
        }

        if (rawJson is null)
        {
            rawJson = await _http.GetStringAsync(MapsJsonUrl, ct);
            await File.WriteAllTextAsync(_cacheFilePath, rawJson, ct);
        }

        return ParseInteractiveCalibrations(rawJson);
    }

    // Internal (not private) so tests can feed raw JSON text directly.
    internal static Dictionary<string, MapCalibration> ParseInteractiveCalibrations(string rawJson)
    {
        var groups = JsonSerializer.Deserialize<List<MapGroupDto>>(rawJson) ?? new List<MapGroupDto>();
        var result = new Dictionary<string, MapCalibration>();

        foreach (var group in groups)
        {
            var interactive = group.Maps.FirstOrDefault(m => m.Projection == "interactive" && m.Transform is not null);
            if (interactive is null)
            {
                continue;
            }

            result[group.NormalizedName] = new MapCalibration
            {
                NormalizedName = group.NormalizedName,
                Key = interactive.Key ?? group.NormalizedName,
                Transform = interactive.Transform,
                CoordinateRotationDegrees = interactive.CoordinateRotation ?? 0f,
                SvgPath = interactive.SvgPath,
                // svgBounds overrides bounds for the base/ground layer only
                // (verified: height layers always use plain bounds) - not
                // modeled separately yet since no current map needs it for
                // the base layer this app renders.
                Bounds = interactive.Bounds,
                HeightRange = interactive.HeightRange is { Length: 2 }
                    ? new MapHeightExtent { MinHeight = interactive.HeightRange[0], MaxHeight = interactive.HeightRange[1] }
                    : null,
                Layers = (interactive.Layers ?? new List<MapLayerDto>()).Select(l => new MapLayer
                {
                    Name = l.Name ?? string.Empty,
                    SvgLayer = l.SvgLayer,
                    Extents = (l.Extents ?? new List<MapExtentDto>())
                        .Where(e => e.Height is { Length: 2 })
                        .Select(e => new MapHeightExtent { MinHeight = e.Height![0], MaxHeight = e.Height[1] })
                        .ToList(),
                }).ToList(),
                // label.position is [x, z] (verified against real Customs/
                // Woods label data), unlike the top-level bounds array
                // which is [lng,lat] i.e. effectively [x,z] too but built
                // via a different, swapped code path in tarkov-dev's own
                // source - these two are coincidentally the same order,
                // not a shared convention to assume elsewhere.
                Pois = (interactive.Labels ?? new List<MapLabelDto>())
                    .Where(l => l.Position is { Length: >= 2 } && l.Text is not null)
                    .Select(l => new MapPoi { Text = l.Text!, X = l.Position![0], Z = l.Position[1] })
                    .ToList(),
            };
        }

        return result;
    }

    private class MapGroupDto
    {
        [JsonPropertyName("normalizedName")]
        public string NormalizedName { get; set; } = string.Empty;

        [JsonPropertyName("maps")]
        public List<MapVariantDto> Maps { get; set; } = new();
    }

    private class MapVariantDto
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("projection")]
        public string? Projection { get; set; }

        [JsonPropertyName("transform")]
        public float[]? Transform { get; set; }

        [JsonPropertyName("coordinateRotation")]
        public float? CoordinateRotation { get; set; }

        [JsonPropertyName("svgPath")]
        public string? SvgPath { get; set; }

        [JsonPropertyName("heightRange")]
        public float[]? HeightRange { get; set; }

        [JsonPropertyName("bounds")]
        public float[][]? Bounds { get; set; }

        [JsonPropertyName("layers")]
        public List<MapLayerDto>? Layers { get; set; }

        [JsonPropertyName("labels")]
        public List<MapLabelDto>? Labels { get; set; }
    }

    private class MapLabelDto
    {
        [JsonPropertyName("position")]
        public float[]? Position { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private class MapLayerDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("svgLayer")]
        public string? SvgLayer { get; set; }

        [JsonPropertyName("extents")]
        public List<MapExtentDto>? Extents { get; set; }
    }

    private class MapExtentDto
    {
        [JsonPropertyName("height")]
        public float[]? Height { get; set; }
    }
}
