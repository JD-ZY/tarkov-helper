namespace TarkovHelper.Core.Position;

public sealed class ScreenshotPositionWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _retryTimer;
    private string? _pendingFolder;

    public event EventHandler<PlayerPosition>? PositionUpdated;

    // Fires once the watcher successfully starts watching a folder that
    // previously did not exist - lets the UI clear a "not found" message
    // without the caller having to poll.
    public event EventHandler? FolderFound;

    // Fires when a new .png appears in the screenshots folder but its
    // filename didn't match the expected position/rotation format - lets
    // the UI surface the raw filename for diagnosis instead of silently
    // doing nothing, since a format drift (different EFT build/locale)
    // would otherwise look identical to "no screenshot taken".
    public event EventHandler<string>? UnparseableScreenshot;

    // Default folder verified: %USERPROFILE%\Documents\Escape From Tarkov\Screenshots.
    // Some users redirect OS-level screenshots elsewhere (e.g. Game Bar),
    // which is a different, unrelated capture path - callers may override.
    public static string GetDefaultScreenshotsFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Escape From Tarkov",
            "Screenshots");

    // The folder does not exist until the player takes their first
    // in-game screenshot, so a missing folder at launch is normal, not
    // fatal - if this returns false, the watcher keeps retrying every few
    // seconds in the background and starts automatically once the folder
    // appears (see StartWithRetry).
    public bool Start(string? screenshotsFolder = null)
    {
        var folder = screenshotsFolder ?? GetDefaultScreenshotsFolder();
        if (!Directory.Exists(folder))
        {
            return false;
        }

        AttachWatcher(folder);
        return true;
    }

    // Same as Start, but if the folder doesn't exist yet, keeps checking
    // in the background and attaches automatically once it appears -
    // covers the common case of the app being launched before the
    // player's first-ever screenshot.
    public bool StartWithRetry(string? screenshotsFolder = null)
    {
        var folder = screenshotsFolder ?? GetDefaultScreenshotsFolder();
        if (Directory.Exists(folder))
        {
            AttachWatcher(folder);
            return true;
        }

        _pendingFolder = folder;
        _retryTimer = new System.Threading.Timer(CheckPendingFolder, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        return false;
    }

    private void CheckPendingFolder(object? state)
    {
        if (_pendingFolder is null || !Directory.Exists(_pendingFolder))
        {
            return;
        }

        var folder = _pendingFolder;
        _pendingFolder = null;
        _retryTimer?.Dispose();
        _retryTimer = null;

        AttachWatcher(folder);
        FolderFound?.Invoke(this, EventArgs.Empty);
    }

    private void AttachWatcher(string folder)
    {
        _watcher = new FileSystemWatcher(folder)
        {
            Filter = "*.png",
            NotifyFilter = NotifyFilters.FileName,
        };
        _watcher.Created += OnScreenshotCreated;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnScreenshotCreated(object sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? string.Empty;
        if (ScreenshotFilenameParser.TryParse(name, out var position))
        {
            PositionUpdated?.Invoke(this, position);
        }
        else
        {
            UnparseableScreenshot?.Invoke(this, name);
        }
    }

    public void Dispose()
    {
        _retryTimer?.Dispose();

        if (_watcher is not null)
        {
            _watcher.Created -= OnScreenshotCreated;
            _watcher.Dispose();
        }
    }
}
