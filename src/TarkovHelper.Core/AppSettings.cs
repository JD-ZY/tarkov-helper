using System.Text.Json;

namespace TarkovHelper.Core;

// Small persisted settings store for user-configurable overrides - e.g. a
// custom screenshots folder path for players whose Documents folder is
// redirected (OneDrive sync, moved to another drive) so the default
// %USERPROFILE%\Documents\Escape From Tarkov\Screenshots guess doesn't apply.
public class AppSettings
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private class SettingsData
    {
        public string? ScreenshotsFolder { get; set; }

        // Item-lookup trigger keybind - persisted as its raw component
        // parts (not a serialized InputBinding struct directly) so this
        // Core-layer class doesn't need to reference the App-layer
        // InputBinding/InputKind/MouseButton/ModifierKey types at all.
        public string? ItemLookupInputKind { get; set; }
        public int? ItemLookupKeyCode { get; set; }
        public string? ItemLookupMouseButton { get; set; }
        public string? ItemLookupModifier { get; set; }
    }

    private SettingsData _data = new();

    public AppSettings(string appDataFolder)
    {
        Directory.CreateDirectory(appDataFolder);
        _filePath = Path.Combine(appDataFolder, "settings.json");
        Load();
    }

    public string? ScreenshotsFolder => _data.ScreenshotsFolder;

    public void SetScreenshotsFolder(string? folder)
    {
        _data.ScreenshotsFolder = folder;
        Save();
    }

    // Raw string/int accessors, not the App-layer InputBinding type itself -
    // keeps this Core project free of a dependency on TarkovHelper.App.
    // Returns null for any component that was never set (first run, or a
    // settings file from before this feature existed), letting the caller
    // fall back to its own default binding.
    public (string Kind, int KeyCode, string MouseButton, string Modifier)? ItemLookupBinding =>
        _data.ItemLookupInputKind is not null
            && _data.ItemLookupMouseButton is not null
            && _data.ItemLookupModifier is not null
            ? (_data.ItemLookupInputKind, _data.ItemLookupKeyCode ?? 0, _data.ItemLookupMouseButton, _data.ItemLookupModifier)
            : null;

    public void SetItemLookupBinding(string kind, int keyCode, string mouseButton, string modifier)
    {
        _data.ItemLookupInputKind = kind;
        _data.ItemLookupKeyCode = keyCode;
        _data.ItemLookupMouseButton = mouseButton;
        _data.ItemLookupModifier = modifier;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _data = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions) ?? new SettingsData();
        }
        catch (JsonException)
        {
            _data = new SettingsData();
        }
    }

    private void Save() =>
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, JsonOptions));
}
