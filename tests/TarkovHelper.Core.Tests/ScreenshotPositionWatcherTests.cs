using TarkovHelper.Core.Position;

namespace TarkovHelper.Core.Tests;

// Exercises the real FileSystemWatcher wiring end-to-end (not just the
// regex parser in isolation) against a real temp directory, proving the
// Created-event -> parse -> PositionUpdated pipeline actually fires.
public class ScreenshotPositionWatcherTests : IDisposable
{
    private readonly string _tempDir;

    public ScreenshotPositionWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TarkovHelperScreenshotTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task NewScreenshotFile_FiresPositionUpdatedWithParsedCoordinates()
    {
        using var watcher = new ScreenshotPositionWatcher();
        var tcs = new TaskCompletionSource<PlayerPosition>();
        watcher.PositionUpdated += (_, position) => tcs.TrySetResult(position);

        var started = watcher.Start(_tempDir);
        Assert.True(started);

        var filename = "2026-08-08[17-53]_-312.09, 1.25, -210.94_0.01107, -0.10934, 0.00122, 0.99394_13.29 (0).png";
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, filename), [0x89, 0x50, 0x4E, 0x47]);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(tcs.Task, completed);

        var position = await tcs.Task;
        Assert.Equal(-312.09f, position.X, precision: 2);
        Assert.Equal(1.25f, position.Y, precision: 2);
        Assert.Equal(-210.94f, position.Z, precision: 2);
    }

    [Fact]
    public void Start_OnMissingFolder_ReturnsFalseRatherThanThrowing()
    {
        using var watcher = new ScreenshotPositionWatcher();

        var started = watcher.Start(Path.Combine(_tempDir, "does-not-exist"));

        Assert.False(started);
    }

    [Fact]
    public async Task NonPngFile_DoesNotFirePositionUpdated()
    {
        using var watcher = new ScreenshotPositionWatcher();
        var fired = false;
        watcher.PositionUpdated += (_, _) => fired = true;

        watcher.Start(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "notes.txt"), "hello");
        await Task.Delay(500);

        Assert.False(fired);
    }

    [Fact]
    public async Task UnrecognizedPngFilename_FiresUnparseableScreenshotWithRawName()
    {
        using var watcher = new ScreenshotPositionWatcher();
        var tcs = new TaskCompletionSource<string>();
        watcher.UnparseableScreenshot += (_, filename) => tcs.TrySetResult(filename);

        watcher.Start(_tempDir);
        const string filename = "screenshot-from-a-different-eft-build.png";
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, filename), [0x89, 0x50, 0x4E, 0x47]);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(tcs.Task, completed);
        Assert.Equal(filename, await tcs.Task);
    }

    [Fact]
    public void StartWithRetry_OnMissingFolder_ReturnsFalseRatherThanThrowing()
    {
        using var watcher = new ScreenshotPositionWatcher();

        var started = watcher.StartWithRetry(Path.Combine(_tempDir, "does-not-exist"));

        Assert.False(started);
    }

    [Fact]
    public async Task StartWithRetry_WhenFolderAppearsLater_AutoAttachesAndFiresFolderFound()
    {
        var pendingFolder = Path.Combine(_tempDir, "not-yet-created");
        using var watcher = new ScreenshotPositionWatcher();
        var folderFoundTcs = new TaskCompletionSource<bool>();
        var positionTcs = new TaskCompletionSource<PlayerPosition>();
        watcher.FolderFound += (_, _) => folderFoundTcs.TrySetResult(true);
        watcher.PositionUpdated += (_, position) => positionTcs.TrySetResult(position);

        var startedImmediately = watcher.StartWithRetry(pendingFolder);
        Assert.False(startedImmediately);

        // Simulates the folder appearing after the player's first
        // screenshot, well after the app was already running.
        Directory.CreateDirectory(pendingFolder);

        var folderFoundCompleted = await Task.WhenAny(folderFoundTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(folderFoundTcs.Task, folderFoundCompleted);

        var filename = "2026-08-08[17-53]_-312.09, 1.25, -210.94_0.01107, -0.10934, 0.00122, 0.99394_13.29 (0).png";
        await File.WriteAllBytesAsync(Path.Combine(pendingFolder, filename), [0x89, 0x50, 0x4E, 0x47]);

        var positionCompleted = await Task.WhenAny(positionTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(positionTcs.Task, positionCompleted);
    }
}
