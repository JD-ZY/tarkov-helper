using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TarkovHelper.Core;
using TarkovHelper.Core.JsonFallback;
using TarkovHelper.Core.Logs;
using TarkovHelper.Core.Maps;
using TarkovHelper.Core.Models;
using TarkovHelper.Core.Position;

namespace TarkovHelper.App;

public partial class MainWindow : Window
{
    private readonly QuestRepository _repository;
    private readonly HideoutRepository _hideoutRepository;
    private readonly AmmoRepository _ammoRepository;
    private readonly JsonTarkovDevClient _itemNamesClient = new();
    private readonly GameLogWatcher _logWatcher = new();
    private readonly AppSettings _settings;
    private ScreenshotPositionWatcher _positionWatcher = new();
    private readonly string _appDataFolder;
    private List<QuestTask> _allTasks = new();
    private List<HideoutStation> _allStations = new();
    private Dictionary<string, string> _itemNames = new();
    private Dictionary<string, ItemDetails> _itemDetails = new();
    private Dictionary<string, QuestTask> _tasksById = new();
    private bool _isLoaded;
    private MapWindow? _mapWindow;
    private string? _currentMapNormalizedName;

    // Tracks which mode's quest data is currently loaded, driven live by
    // GameLogWatcher.GameModeChanged (parsed from application.log's
    // "Session mode: ..." line). Starts Regular since that's the more
    // common case and matches every existing install's unsuffixed cache/
    // progress files - if the player is actually in PvE, the mode-change
    // event fires and reloads before they'd notice, same startup race
    // every other live-log-driven field here already has.
    private GameMode _currentGameMode = GameMode.Regular;

    // The map the map window is actually displaying, which can differ from
    // _currentMapNormalizedName (the live raid map) once the user picks a
    // different map from MapWindow's dropdown - objective markers must be
    // filtered against whichever map is really on screen, not the raid map,
    // or markers would silently vanish while browsing a different map.
    private string? _displayedMapNormalizedName;
    private GlobalInputHook? _itemLookupInputHook;
    private ItemHoverLookup? _hoverLookup;

    // Set by the hover-lookup hotkey to the exact matched item's ID, so
    // ApplyFilter can use the reliable ID-based FindQuestNeedsForItemId
    // lookup instead of substring-matching the item name against
    // objective description text - real bug fixed: text search missed
    // "Car battery" against Car Repair's "Find Car batteries in raid"
    // description (singular vs. plural), and never included quests that
    // aren't active yet at all, since FindTasksReferencingItem was the
    // only lookup ever wired into the search box despite
    // FindQuestNeedsForItemId already existing and covering both gaps.
    // Cleared whenever the user types a search manually, since a
    // hand-typed query has no known item ID to look up by.
    private string? _itemLookupItemId;

    public MainWindow()
    {
        InitializeComponent();

        _appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovHelper");
        _repository = new QuestRepository(new TarkovDevClient(), _appDataFolder);
        _hideoutRepository = new HideoutRepository(new JsonTarkovDevClient(), _appDataFolder);
        _ammoRepository = new AmmoRepository(new JsonTarkovDevClient(), _appDataFolder);
        _settings = new AppSettings(_appDataFolder);

        // Order matters: wire the log watcher's event handlers and replay
        // history BEFORE quests load, so ReplayHistory's TaskStatusChanged
        // events land in QuestRepository's persisted active/completed sets
        // in time for LoadTasksAsync's ApplyLocalProgress to pick them up -
        // otherwise a first-ever run would show 0 active quests until the
        // next full app restart.
        Loaded += (_, _) => StartLogWatcher();
        Loaded += async (_, _) => await LoadTasksAsync(forceRefresh: false);
        Loaded += (_, _) => StartPositionWatcher();
        Loaded += async (_, _) => await LoadHideoutAsync(forceRefresh: false);
        Loaded += async (_, _) => await LoadAmmoAsync(forceRefresh: false);
        Loaded += async (_, _) => await LoadItemNamesAsync();
        Loaded += (_, _) => StartItemLookupHotkey();
        Closed += (_, _) => _logWatcher.Dispose();
        Closed += (_, _) => _positionWatcher.Dispose();
        Closed += (_, _) => _itemLookupInputHook?.Dispose();
        Closed += (_, _) => _hoverLookup?.Dispose();
    }

    // Set whenever the item-name catalog fails to load, so a caller can
    // tell "OCR read the wrong text" apart from "OCR read the right text,
    // but had nothing to match it against" - previously this failure was
    // swallowed entirely with no visible trace, confirmed as the real
    // cause of a specific miss ("Set of files \"Master\"" OCR'd perfectly
    // from a real capture, but matched nothing because tarkov.dev's
    // items_en endpoint returned 404 at the time and the empty-dictionary
    // fallback silently disabled OCR-based matching for the whole
    // session, with no error surfaced anywhere).
    public string? LastItemNamesLoadError { get; private set; }

    private string ItemNamesCacheFilePath => Path.Combine(_appDataFolder, "item-names-cache.json");

    private async Task LoadItemNamesAsync()
    {
        // Real bug this retry loop fixes: tarkov.dev's item data endpoint
        // is occasionally unavailable (confirmed directly - a live request
        // returned 404 during investigation of a real missed lookup), and
        // the previous single-attempt fetch treated that exactly the same
        // as a permanent failure - falling back to an empty name
        // dictionary for the rest of the session, silently disabling
        // OCR-based item lookup entirely until the app was restarted.
        // Retrying with backoff gives a transient outage a real chance to
        // resolve within the same session instead of requiring a restart.
        const int maxAttempts = 3;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _itemNames = await _itemNamesClient.GetItemNamesAsync();
                _itemDetails = await _itemNamesClient.GetItemDetailsAsync();
                LastItemNamesLoadError = null;

                try
                {
                    await File.WriteAllTextAsync(
                        ItemNamesCacheFilePath,
                        System.Text.Json.JsonSerializer.Serialize(_itemNames));
                }
                catch (IOException)
                {
                    // Best-effort - a failed cache write must not fail an
                    // otherwise-successful live fetch.
                }

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        // Real bug this disk-cache fallback fixes: unlike quest/hideout
        // data (which persist a cache and fall back to yesterday's data on
        // fetch failure), the item-name dictionary used for OCR-based item
        // lookup had NO disk cache at all - it was fetched fresh every
        // launch, so a fetch failure meant OCR matching was completely
        // disabled for the whole session with nothing to fall back to,
        // even with the retry above. Falling back to a locally cached copy
        // (from any earlier successful launch) degrades this to "slightly
        // stale item names" instead of "item lookup off entirely" -
        // matching the same resilience quests/hideout already have.
        try
        {
            if (File.Exists(ItemNamesCacheFilePath))
            {
                var cached = await File.ReadAllTextAsync(ItemNamesCacheFilePath);
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(cached);
                if (parsed is { Count: > 0 })
                {
                    _itemNames = parsed;
                    _itemDetails = new Dictionary<string, ItemDetails>();
                    LastItemNamesLoadError = $"using cached item names from a previous session - live fetch failed ({lastError?.Message ?? "unknown error"})";
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the empty-dictionary fallback below - a
            // corrupt/unreadable cache is no better than no cache.
        }

        // Item lookup is a display enhancement, not core functionality -
        // still fail soft (the hotkey falls back to icon matching rather
        // than throwing), but the failure is now visible via
        // LastItemNamesLoadError instead of silently swallowed.
        _itemNames = new Dictionary<string, string>();
        _itemDetails = new Dictionary<string, ItemDetails>();
        LastItemNamesLoadError = lastError?.Message ?? "unknown error";
    }

    // The tessdata folder ships alongside the exe (CopyToOutputDirectory in
    // the csproj) - resolved relative to the running assembly so it works
    // both from the build output and from a published single-file exe,
    // where AppContext.BaseDirectory is the folder the exe was extracted
    // next to, not a path inside the bundled exe itself.
    private static string TessdataPath => Path.Combine(AppContext.BaseDirectory, "tessdata");
    private static string IconsPath => Path.Combine(AppContext.BaseDirectory, "icons");

    private void StartItemLookupHotkey()
    {
        try
        {
            var diagnosticsPath = Path.Combine(_appDataFolder, "diagnostics");
            _hoverLookup = new ItemHoverLookup(TessdataPath, IconsPath, diagnosticsPath);

            // Default Alt+Click, not a hover+hotkey - deliberately
            // click/key-triggered (fires on button-down/key-down) so the
            // capture happens at the moment of the press, before EFT's
            // item-name tooltip has had time to render/settle. Real bug
            // this fixes: reacting to an ALREADY-hovering cursor (the old
            // Ctrl+Alt+I hotkey model) let the tooltip fully render and
            // settle exactly where it's most likely to overlap the
            // hovered cell, corrupting the grid-detection/template-match
            // crop and producing wildly wrong matches (a magazine matched
            // to an unrelated armor plate). This is the same interaction
            // model RatScanner itself uses (Shift+Click) for exactly this
            // reason. User-configurable via ItemLookupHotkeyButton -
            // GlobalInputHook.Binding is read from AppSettings if a custom
            // one was saved, otherwise falls back to Alt+LeftClick.
            _itemLookupInputHook = new GlobalInputHook();
            if (_settings.ItemLookupBinding is { } savedBinding)
            {
                _itemLookupInputHook.Binding = ParseSavedBinding(savedBinding);
            }

            _itemLookupInputHook.TriggerActivated += (_, _) => OnItemLookupHotkeyPressed();
            UpdateItemLookupHotkeyButtonText();

            if (!_itemLookupInputHook.IsInstalled)
            {
                RaidStatusText.Text = "Item lookup unavailable - couldn't install input hook";
            }
        }
        catch (Exception ex)
        {
            // Tesseract failing to initialize (e.g. corrupted tessdata,
            // missing native DLL) shouldn't take down the rest of the app -
            // this feature just won't be available.
            RaidStatusText.Text = $"Item lookup unavailable: {ex.Message}";
        }
    }

    private static InputBinding ParseSavedBinding((string Kind, int KeyCode, string MouseButton, string Modifier) saved)
    {
        var kind = Enum.TryParse<InputKind>(saved.Kind, out var parsedKind) ? parsedKind : InputKind.MouseButton;
        var mouseButton = Enum.TryParse<MouseButton>(saved.MouseButton, out var parsedButton) ? parsedButton : MouseButton.Left;
        var modifier = Enum.TryParse<ModifierKey>(saved.Modifier, out var parsedModifier) ? parsedModifier : ModifierKey.Alt;
        return new InputBinding(kind, saved.KeyCode, mouseButton, modifier);
    }

    private void UpdateItemLookupHotkeyButtonText()
    {
        if (_itemLookupInputHook is not null)
        {
            ItemLookupHotkeyButton.Content = $"Item lookup: {_itemLookupInputHook.Binding}";
        }
    }

    // "Click a button, it looks out for your next input, and that becomes
    // your keybind" - arms GlobalInputHook's one-shot capture mode, then
    // saves whatever key or mouse button (plus modifier) the user presses
    // next as the new trigger, live (no restart needed) and persisted via
    // AppSettings so it survives the next launch.
    private void OnChangeItemLookupHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (_itemLookupInputHook is null)
        {
            return;
        }

        ItemLookupHotkeyButton.Content = "Press any key or click...";
        ItemLookupHotkeyButton.IsEnabled = false;

        _itemLookupInputHook.CaptureNextInput(captured =>
        {
            // The hook invokes this callback from its own low-level hook
            // thread, not the UI thread - every UI/settings touch below
            // must be marshaled back via Dispatcher or WPF throws.
            Dispatcher.Invoke(() =>
            {
                _itemLookupInputHook.Binding = captured;
                _settings.SetItemLookupBinding(
                    captured.Kind.ToString(),
                    captured.KeyCode,
                    captured.MouseButtonValue.ToString(),
                    captured.Modifier.ToString());

                UpdateItemLookupHotkeyButtonText();
                ItemLookupHotkeyButton.IsEnabled = true;
            });
        });
    }

    // Drives the existing search box + "search as item" flow instead of a
    // separate popup window - reuses UI that already exists and is already
    // tested by hand, and avoids introducing a second window with its own
    // lifecycle (a standalone always-on-top result popup was tried first
    // and repeatedly crashed on WPF window-deactivation reentrancy edge
    // cases - see git history/ItemLookupPopup, since removed). Result
    // details (price, hideout needs) go in ItemLookupResultBanner - a
    // prominent, high-contrast panel above the quest grid - rather than
    // the small gray top-bar StatusText, which was easy to miss entirely.
    private void OnItemLookupHotkeyPressed()
    {
        if (_hoverLookup is null)
        {
            return;
        }

        // ItemHoverLookup tries icon template matching first (reliable in
        // dense stash/inventory grids, where pure OCR previously confused
        // neighboring item captions only ~60px apart) and falls back to
        // OCR (which already works for the single floating tooltip shown
        // when hovering loot in a raid, where there's no grid to detect).
        var match = _hoverLookup.Resolve(_itemNames);

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();

        if (match is null)
        {
            ItemLookupResultBanner.Visibility = Visibility.Collapsed;
            var diagnostic = _hoverLookup.LastIconMatchDiagnostic;
            if (LastItemNamesLoadError is { } loadError)
            {
                // Surfaced ahead of the icon-match diagnostic: if the item
                // name catalog never loaded, OCR-based matching (which
                // runs first) had nothing to match against no matter how
                // correctly it read the on-screen text - worth knowing
                // that distinctly from "OCR/icon-matching itself failed."
                diagnostic = diagnostic is null
                    ? $"item name list failed to load ({loadError})"
                    : $"item name list failed to load ({loadError}); {diagnostic}";
            }

            StatusText.Text = diagnostic is not null
                ? $"Item lookup: couldn't identify an item near the cursor ({diagnostic})"
                : "Item lookup: couldn't identify an item near the cursor";
            return;
        }

        var (itemId, itemName, _, _) = match.Value;
        ShowItemLookupResult(itemId, itemName);
    }

    // Populates and shows ItemLookupResultBanner for a known item ID/name -
    // shared by both the Alt+Click hover hotkey (OnItemLookupHotkeyPressed,
    // above) and the top-bar autocomplete search box
    // (OnItemSearchSelectionChanged, below), so picking an item by typing
    // its name produces the exact same price/hideout-need result the
    // hotkey does, not a separate/lesser display.
    private void ShowItemLookupResult(string itemId, string itemName)
    {
        // Set the banner's own name text and _itemLookupItemId BEFORE
        // touching SearchBox.Text - that assignment fires TextChanged ->
        // ApplyFilter synchronously, and ApplyFilter both compares the new
        // search text against ItemLookupResultName.Text (to decide whether
        // to collapse the banner as stale) and reads _itemLookupItemId (to
        // use the reliable ID-based lookup instead of text search). Setting
        // both first keeps that logic correct regardless of ordering
        // elsewhere in this method.
        ItemLookupResultName.Text = itemName;
        _itemLookupItemId = itemId;
        ItemLookupModeCheckBox.IsChecked = true;
        SearchBox.Text = itemName;

        // Uncheck "active quests only" so quests that need this item but
        // aren't started yet (or are still locked behind a prerequisite)
        // show up too - a "keep or sell" decision needs the full picture,
        // not just what's immediately actionable.
        ActiveOnlyCheckBox.IsChecked = false;

        var priceParts = new List<string>();
        if (_itemDetails.TryGetValue(itemId, out var details))
        {
            if (details.FleaPriceRub is { } fleaPrice)
            {
                priceParts.Add($"Flea: ~{fleaPrice:N0}₽");
            }
            if (details.BestTraderOffer is { } bestOffer)
            {
                priceParts.Add($"Best trader: {bestOffer.TraderName} {bestOffer.PriceRub:N0}₽");
            }
        }
        ItemLookupResultPrices.Text = priceParts.Count > 0 ? string.Join("   ", priceParts) : "No price data available";

        var hideoutNeeds = ItemLookup.FindHideoutNeedsForItemId(_allStations, itemId);
        ItemLookupResultHideout.Text = hideoutNeeds.Count > 0
            ? "Hideout: " + string.Join(", ", hideoutNeeds
                .OrderByDescending(n => n.IsAvailableNow)
                .Select(n => $"{n.SourceName} {n.DetailText}" + (n.IsAvailableNow ? string.Empty : " (later)")))
            : string.Empty;

        ItemLookupResultBanner.Visibility = Visibility.Visible;

        // The result banner lives inside the Quests & Item Lookup tab, so
        // switching to that tab when a search-box selection is made means
        // the result is actually visible immediately, not hidden behind
        // whichever tab (e.g. Ammo) happened to be open when the user
        // typed the search.
        QuestsTabItem.IsSelected = true;
    }

    // Guards against ComboBox's own SelectionChanged firing while THIS
    // handler is still repopulating ItemsSource in response to typing -
    // WPF can raise a spurious SelectionChanged (selection cleared, then
    // re-set) as a side effect of swapping ItemsSource out from under an
    // open dropdown, which would otherwise re-enter
    // OnItemSearchSelectionChanged with a stale/irrelevant selection.
    private bool _isRepopulatingItemSearch;

    private void OnItemSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Real bug this guard fixes: OnItemSearchSelectionChanged sets
        // .Text as part of its own post-selection cleanup, which fires
        // this handler synchronously (TextBoxBase.TextChanged) - without
        // this check, that cleanup's Text assignment was treated as a
        // brand new search, re-filtering _itemNames and reopening the
        // dropdown right after OnItemSearchSelectionChanged had just
        // closed it.
        if (_isRepopulatingItemSearch)
        {
            return;
        }

        var query = ItemSearchComboBox.Text;
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            // Real bug this length gate avoids: filtering the full
            // ~5000-item catalog against a single typed character matches
            // hundreds of items, which is both slow to render and useless
            // as a narrowed-down choice - wait for at least 2 characters
            // before populating the dropdown at all.
            ItemSearchComboBox.ItemsSource = null;
            ItemSearchComboBox.IsDropDownOpen = false;
            return;
        }

        var matches = _itemNames
            .Where(kv => kv.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.IndexOf(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(kv => kv.Value.Length)
            .Take(25)
            .Select(kv => new ItemSearchEntry(kv.Key, kv.Value))
            .ToList();

        _isRepopulatingItemSearch = true;
        try
        {
            ItemSearchComboBox.ItemsSource = matches;
            // Re-set after ItemsSource swap: assigning ItemsSource on an
            // editable ComboBox can otherwise overwrite the text the user
            // is actively typing with the empty/first-item value.
            ItemSearchComboBox.Text = query;

            // Real bug this fixes: opening the dropdown (IsDropDownOpen =
            // true, below) makes WPF auto-select the ENTIRE text of the
            // editable part as a side effect of the popup taking focus -
            // confirmed directly (typing a 3rd character replaced
            // everything already typed, because it landed on top of a
            // full-text selection, not an appended keystroke). Setting
            // SelectionStart alone doesn't clear SelectionLength, and
            // doing it BEFORE IsDropDownOpen is set doesn't survive that
            // side effect anyway - the fix has to explicitly clear the
            // selection to a zero-length caret AFTER the dropdown is
            // opened, undoing WPF's auto-select-all on the same
            // dispatcher pass rather than racing it.
            ItemSearchComboBox.IsDropDownOpen = matches.Count > 0;

            if (ItemSearchComboBox.Template.FindName("PART_EditableTextBox", ItemSearchComboBox) is TextBox editableTextBox)
            {
                editableTextBox.SelectionStart = query.Length;
                editableTextBox.SelectionLength = 0;
            }
        }
        finally
        {
            _isRepopulatingItemSearch = false;
        }
    }

    private void OnItemSearchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRepopulatingItemSearch)
        {
            return;
        }

        if (ItemSearchComboBox.SelectedItem is not ItemSearchEntry selected)
        {
            return;
        }

        ShowItemLookupResult(selected.Id, selected.Name);

        // Real bug this guard fixes: setting .Text on an editable
        // ComboBox fires TextBoxBase.TextChanged synchronously (re-
        // entering OnItemSearchTextChanged), which - since ItemsSource
        // had already been nulled out on the line before - filtered
        // _itemNames fresh against the picked name and reopened the
        // dropdown right after this method had just closed it. Wrapping
        // both mutations in the same reentrancy flag OnItemSearchTextChanged
        // already checks makes it a no-op during this cleanup, the same
        // way it's already a no-op during the initial populate.
        _isRepopulatingItemSearch = true;
        try
        {
            // Clear so the box shows the picked name cleanly (via
            // ShowItemLookupResult -> SearchBox.Text, a separate control)
            // and is ready for the next search rather than leaving the
            // dropdown open on a stale, already-acted-on match list.
            ItemSearchComboBox.ItemsSource = null;
            ItemSearchComboBox.Text = selected.Name;
            ItemSearchComboBox.IsDropDownOpen = false;
        }
        finally
        {
            _isRepopulatingItemSearch = false;
        }
    }

    private readonly record struct ItemSearchEntry(string Id, string Name);

    private async Task LoadHideoutAsync(bool forceRefresh)
    {
        HideoutStatusText.Text = "Loading hideout data...";
        RefreshHideoutButton.IsEnabled = false;

        try
        {
            var stations = await _hideoutRepository.LoadStationsAsync(forceRefresh);
            _allStations = stations;
            HideoutGrid.ItemsSource = stations
                .OrderBy(s => s.Name)
                .Select(s => new HideoutStationRow(s))
                .ToList();
            HideoutStatusText.Text = $"{stations.Count} stations loaded";
        }
        catch (Exception ex)
        {
            HideoutStatusText.Text = $"Failed to load hideout data: {ex.Message}";
        }
        finally
        {
            RefreshHideoutButton.IsEnabled = true;
        }
    }

    private async void OnRefreshHideoutClick(object sender, RoutedEventArgs e) =>
        await LoadHideoutAsync(forceRefresh: true);

    // Full, unfiltered set from the last successful load - kept so the
    // search box (OnAmmoSearchTextChanged) can re-filter/re-group without
    // a fresh network fetch on every keystroke.
    private List<AmmoRow> _allAmmoRows = new();

    private async Task LoadAmmoAsync(bool forceRefresh)
    {
        AmmoStatusText.Text = "Loading ammo data...";
        RefreshAmmoButton.IsEnabled = false;

        try
        {
            var ammo = await _ammoRepository.LoadAmmoAsync(forceRefresh);
            _allAmmoRows = ammo
                .Select(a => new AmmoRow(a))
                .OrderBy(r => r.Caliber)
                .ThenByDescending(r => r.PenetrationPower)
                .ToList();

            ApplyAmmoFilter();
            AmmoStatusText.Text = $"{ammo.Count} rounds loaded";
        }
        catch (Exception ex)
        {
            AmmoStatusText.Text = $"Failed to load ammo data: {ex.Message}";
        }
        finally
        {
            RefreshAmmoButton.IsEnabled = true;
        }
    }

    // Filters _allAmmoRows by name/caliber against AmmoSearchBox's current
    // text, then re-groups the result - called on load, on refresh, and on
    // every search-box keystroke, so a search never needs a fresh fetch.
    // Real bug this fixes: the ammo tab had no way to narrow ~200 rounds
    // down to a specific caliber/name at all - every group had to be
    // scrolled through manually to find one round.
    private void ApplyAmmoFilter()
    {
        var query = AmmoSearchBox.Text;
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allAmmoRows
            : _allAmmoRows
                .Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.Caliber.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        // DataGrid grouping requires an ICollectionView with a
        // GroupDescription, not a plain List<T> assigned directly
        // (that's sufficient for the ungrouped Quest/Hideout grids, but
        // grouping is opt-in via CollectionViewSource) - grouped by
        // Caliber to match the requested "ammo chart grouped by ammo
        // type" layout; rows within each caliber are already
        // pre-sorted by penetration power (highest first) from
        // LoadAmmoAsync, and filtering preserves that order since it's
        // a plain Where() over an already-sorted list.
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(filtered);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(AmmoRow.Caliber)));
        AmmoGrid.ItemsSource = view;
    }

    private void OnAmmoSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyAmmoFilter();

    private async void OnRefreshAmmoClick(object sender, RoutedEventArgs e) =>
        await LoadAmmoAsync(forceRefresh: true);

    private void OnHideoutCellEditEnding(object? sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        // Fires before the binding commits the new value to the row, so
        // persist on the next dispatcher pass once CurrentLevel has
        // actually been updated by the binding.
        if (e.Row.Item is not HideoutStationRow row)
        {
            return;
        }

        Dispatcher.InvokeAsync(() => _hideoutRepository.SetStationLevel(row.Station.Id, row.CurrentLevel));
    }

    private void OnOpenMapClick(object sender, RoutedEventArgs e) => ShowMapWindow();

    // Creates the map window if it doesn't exist yet (or was closed), then
    // brings it to front - shared by the manual "Open map" button and by
    // ShowMapOnScreenshot below, which calls this automatically every time
    // a position update arrives so the map surfaces the same way the item
    // lookup hotkey brings the main window forward.
    private void ShowMapWindow()
    {
        if (_mapWindow is null || !_mapWindow.IsLoaded)
        {
            _mapWindow = new MapWindow(_appDataFolder);
            _mapWindow.Closed += (_, _) => _mapWindow = null;
            _mapWindow.MapSelectionChanged += OnMapWindowSelectionChanged;
            _displayedMapNormalizedName = _currentMapNormalizedName;
            if (_currentMapNormalizedName is not null)
            {
                _ = _mapWindow.SetCurrentMapAsync(_currentMapNormalizedName);
            }

            UpdateObjectiveMarkers();
        }

        _mapWindow.Show();
        _mapWindow.Activate();
    }

    // Recomputes which active-quest objectives have a known position on the
    // currently DISPLAYED map (which may differ from the live raid map if
    // the user picked a different one from MapWindow's dropdown) and pushes
    // them to the map window - called whenever quests load/change, or the
    // displayed map changes, since either input can change the result.
    private void UpdateObjectiveMarkers()
    {
        if (_mapWindow is null || _displayedMapNormalizedName is null)
        {
            return;
        }

        var markers = ObjectiveMarkerFactory.BuildForMap(_allTasks, _displayedMapNormalizedName);
        _mapWindow.SetObjectiveMarkers(markers);
    }

    // Fires when the user picks a map from MapWindow's own dropdown
    // (including switching back to "Follow current raid", signaled by a
    // null normalizedName) - keeps marker filtering in sync with whatever
    // map is actually on screen, and re-asserts the live raid map when the
    // user un-overrides.
    private void OnMapWindowSelectionChanged(object? sender, string? normalizedName)
    {
        Dispatcher.Invoke(() =>
        {
            if (normalizedName is null)
            {
                _displayedMapNormalizedName = _currentMapNormalizedName;
                if (_currentMapNormalizedName is not null)
                {
                    _ = _mapWindow?.SetCurrentMapAsync(_currentMapNormalizedName);
                }
            }
            else
            {
                _displayedMapNormalizedName = normalizedName;
            }

            UpdateObjectiveMarkers();
        });
    }

    private void AttachPositionWatcherHandlers()
    {
        _positionWatcher.PositionUpdated += (_, position) => Dispatcher.Invoke(() =>
        {
            PositionText.Text = $"Position: {position.X:F1}, {position.Y:F1}, {position.Z:F1} (yaw {position.YawDegrees:F0}°)";
            ChooseScreenshotsFolderButton.Visibility = Visibility.Collapsed;
            ShowMapWindow();
            _mapWindow?.UpdatePosition(position);
        });

        _positionWatcher.FolderFound += (_, _) => Dispatcher.Invoke(() =>
        {
            PositionText.Text = "Position: waiting for a screenshot...";
            ChooseScreenshotsFolderButton.Visibility = Visibility.Collapsed;
        });

        // A screenshot arrived but its filename didn't match the expected
        // "<date>[<time>]_<x, y, z>_<qx, qy, qz, qw> (n).png" pattern -
        // most likely a different EFT build/locale writing a slightly
        // different format. Surfacing the raw name (rather than staying
        // silent) turns "position never updates" from a mystery into
        // something diagnosable.
        _positionWatcher.UnparseableScreenshot += (_, filename) => Dispatcher.Invoke(() =>
            PositionText.Text = $"Position: couldn't read '{filename}'");
    }

    private void StartPositionWatcher()
    {
        AttachPositionWatcherHandlers();

        // The screenshots folder only exists after the player has taken at
        // least one in-game screenshot, so a missing folder on first launch
        // is normal, not an error - keep retrying in the background rather
        // than giving up, since the app is commonly opened before that
        // first screenshot is taken.
        if (!_positionWatcher.StartWithRetry(_settings.ScreenshotsFolder))
        {
            PositionText.Text = "Position: screenshots folder not found";
            ChooseScreenshotsFolderButton.Visibility = Visibility.Visible;
        }
    }

    private void OnChooseScreenshotsFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select your Escape From Tarkov Screenshots folder",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        _settings.SetScreenshotsFolder(dialog.SelectedPath);
        _positionWatcher.Dispose();
        _positionWatcher = new ScreenshotPositionWatcher();
        AttachPositionWatcherHandlers();

        if (_positionWatcher.StartWithRetry(dialog.SelectedPath))
        {
            PositionText.Text = "Position: waiting for a screenshot...";
            ChooseScreenshotsFolderButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            PositionText.Text = "Position: screenshots folder not found";
        }
    }

    private void StartLogWatcher()
    {
        // Fires whenever the game's own "Session mode: ..." line changes
        // mode (verified against a real log: this fires per matchmaking
        // attempt, well before a raid's own map/mode lines, and again if
        // the player backs out to the menu and queues a different mode) -
        // reloads quest data for the new mode's separate tarkov.dev dataset
        // and separate local progress files. Skips the reload if the mode
        // didn't actually change (GameLogWatcher already dedupes identical
        // consecutive modes, but this handler doesn't rely on that).
        _logWatcher.GameModeChanged += (_, e) => Dispatcher.Invoke(async () =>
        {
            if (_currentGameMode == e.Mode)
            {
                return;
            }

            _currentGameMode = e.Mode;
            await LoadTasksAsync(forceRefresh: false);
        });

        _logWatcher.MapLoading += (_, e) => Dispatcher.Invoke(() =>
            RaidStatusText.Text = $"Loading map: {e.ScenePath}");

        _logWatcher.MapLoaded += (_, e) => Dispatcher.Invoke(() =>
        {
            RaidStatusText.Text = $"Map: {e.MapNameId ?? "unknown"} ({(e.IsOnline ? "online" : "offline")})";

            var normalizedName = e.MapNameId is not null ? MapNameResolver.ToNormalizedName(e.MapNameId) : null;
            _currentMapNormalizedName = normalizedName;
            if (normalizedName is not null)
            {
                _ = _mapWindow?.SetCurrentMapAsync(normalizedName);

                // Only follow into marker-filtering too if the user hasn't
                // pinned the dropdown to a different map - mirrors
                // SetCurrentMapAsync's own override guard above, which
                // already silently ignored that call in the pinned case.
                if (_mapWindow is null || _mapWindow.IsFollowingRaid)
                {
                    _displayedMapNormalizedName = normalizedName;
                }
            }

            UpdateObjectiveMarkers();
        });

        _logWatcher.RaidStarting += (_, _) => Dispatcher.Invoke(() =>
            RaidStatusText.Text = "Raid starting...");

        _logWatcher.RaidStarted += (_, _) => Dispatcher.Invoke(() =>
            RaidStatusText.Text = "In raid");

        _logWatcher.RaidExited += (_, e) => Dispatcher.Invoke(() =>
            RaidStatusText.Text = $"Raid ended ({e.Location ?? "unknown"})");

        // Bootstrap quest state from every retained past session before
        // live-tailing begins - EFT has no separate save file for quest
        // progress, so this is the only way to recover quests accepted
        // before this app was ever running. A temporary collector batches
        // the (potentially hundreds of) historical events into one write
        // via QuestRepository.ApplyStatusHistory, instead of the live
        // per-event handler's one-file-read-write-per-call cost.
        var history = new List<(string TaskId, QuestTaskStatus Status, GameMode Mode)>();
        void CollectHistoryEvent(object? _, TaskStatusChangedEventArgs e) => history.Add((e.TaskId, e.Status, e.Mode));

        _logWatcher.TaskStatusChanged += CollectHistoryEvent;
        _logWatcher.ReplayHistory();
        _logWatcher.TaskStatusChanged -= CollectHistoryEvent;

        // Historical sessions can span both modes (e.g. the player has
        // played both PvE and PvP over time), so each mode's transitions
        // must land in that mode's own progress files rather than being
        // dumped together into whichever mode happens to be current.
        foreach (var modeGroup in history.GroupBy(h => h.Mode))
        {
            _repository.ApplyStatusHistory(
                modeGroup.Select(h => (h.TaskId, h.Status)),
                modeGroup.Key);
        }

        _logWatcher.TaskStatusChanged += (_, e) => Dispatcher.Invoke(() => OnTaskStatusChanged(e));

        // EFT may not be installed on this machine, or the player has never
        // launched it (no session log folder exists yet) - both are normal,
        // expected conditions, not errors, so degrade quietly rather than
        // failing app startup.
        if (!_logWatcher.Start())
        {
            RaidStatusText.Text = "Raid tracking: EFT logs not found";
        }
    }

    private void OnTaskStatusChanged(TaskStatusChangedEventArgs e)
    {
        var task = _allTasks.FirstOrDefault(t => t.Id == e.TaskId);

        switch (e.Status)
        {
            case QuestTaskStatus.Started:
                _repository.SetTaskActive(e.TaskId, true, e.Mode);
                if (task is not null)
                {
                    task.IsActive = true;
                }
                break;

            case QuestTaskStatus.Finished:
                _repository.SetTaskComplete(e.TaskId, true, e.Mode);
                if (task is not null)
                {
                    task.IsComplete = true;
                    task.IsActive = false;
                }
                break;

            case QuestTaskStatus.Failed:
                _repository.SetTaskActive(e.TaskId, false, e.Mode);
                if (task is not null)
                {
                    task.IsActive = false;
                }
                break;
        }

        ApplyFilter();
        UpdateObjectiveMarkers();
    }

    private async Task LoadTasksAsync(bool forceRefresh)
    {
        StatusText.Text = "Loading quests...";
        RefreshButton.IsEnabled = false;

        try
        {
            _allTasks = await _repository.LoadTasksAsync(_currentGameMode, forceRefresh);
            _tasksById = QuestAvailability.IndexById(_allTasks);

            var missingCount = _repository.CountActiveIdsMissingFrom(_allTasks, _currentGameMode);
            StatusText.Text = missingCount > 0
                ? $"{_allTasks.Count} quests loaded ({missingCount} active quest(s) not yet in tarkov.dev's data - too new)"
                : $"{_allTasks.Count} quests loaded";

            _isLoaded = true;
            ApplyFilter();
            UpdateObjectiveMarkers();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load quests: {ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        if (!_isLoaded)
        {
            return;
        }

        var searchText = SearchBox.Text?.Trim() ?? string.Empty;
        var availableOnly = AvailableOnlyCheckBox.IsChecked == true;
        var activeOnly = ActiveOnlyCheckBox.IsChecked == true;
        var hideComplete = HideCompleteCheckBox.IsChecked == true;
        var itemLookupMode = ItemLookupModeCheckBox.IsChecked == true;

        // The banner (and the ID-based lookup below) reflect one specific
        // hotkey-matched item - once the user turns off item-lookup mode
        // or edits the search text away from that exact item, both go
        // stale: the banner is hidden, and _itemLookupItemId is cleared so
        // a hand-typed query falls back to text search instead of
        // (incorrectly) keeping the old hotkey match's item ID.
        var matchesHotkeyResult = itemLookupMode && string.Equals(searchText, ItemLookupResultName.Text, StringComparison.OrdinalIgnoreCase);
        if (!matchesHotkeyResult)
        {
            ItemLookupResultBanner.Visibility = Visibility.Collapsed;
            _itemLookupItemId = null;
        }

        IEnumerable<QuestTask> filtered;

        if (itemLookupMode && _itemLookupItemId is not null)
        {
            // Reliable exact-ID match (from the hover-lookup hotkey) -
            // covers every incomplete quest including ones not yet active,
            // and doesn't depend on the item name appearing verbatim
            // (same form/plurality) inside an objective's description text.
            filtered = ItemLookup.FindTasksNeedingItemId(_allTasks, _itemLookupItemId);
        }
        else if (itemLookupMode)
        {
            filtered = ItemLookup.FindTasksReferencingItem(_allTasks, searchText);
        }
        else
        {
            filtered = _allTasks;
            if (!string.IsNullOrEmpty(searchText))
            {
                filtered = filtered.Where(t => t.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (hideComplete)
        {
            filtered = filtered.Where(t => !t.IsComplete);
        }

        if (availableOnly)
        {
            filtered = filtered.Where(t => QuestAvailability.IsAvailable(t, _tasksById));
        }

        if (activeOnly)
        {
            filtered = filtered.Where(t => t.IsActive);
        }

        var items = filtered
            .OrderBy(t => t.MinPlayerLevel ?? 0)
            .ThenBy(t => t.Name)
            .Select(t => new QuestListItem(t))
            .ToList();

        foreach (var item in items)
        {
            item.PropertyChanged += OnQuestItemChanged;
        }

        QuestGrid.ItemsSource = items;
        CountText.Text = $"Showing {items.Count} of {_allTasks.Count} quests";
    }

    private void OnQuestItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not QuestListItem item || e.PropertyName != nameof(QuestListItem.IsComplete))
        {
            return;
        }

        if (item.Task.Id is not null)
        {
            _repository.SetTaskComplete(item.Task.Id, item.IsComplete);
        }

        // Completing a task can unlock others, so availability filtering
        // needs to be recomputed rather than just leaving the grid stale.
        ApplyFilter();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadTasksAsync(forceRefresh: true);

    private void OnAlwaysOnTopChanged(object sender, RoutedEventArgs e) =>
        Topmost = AlwaysOnTopCheckBox.IsChecked == true;
}
