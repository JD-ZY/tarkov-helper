using TarkovHelper.Core.Logs;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.Tests;

// Verifies ReplayHistory against real files on disk (not just in-memory
// ProcessChunk calls), since the actual bug class here would be file
// discovery/read-sharing across multiple session folders, not the already-
// tested regex/JSON parsing itself.
public class GameLogWatcherHistoryTests : IDisposable
{
    private readonly string _logsRoot;

    public GameLogWatcherHistoryTests()
    {
        _logsRoot = Path.Combine(Path.GetTempPath(), "TarkovHelperHistoryTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_logsRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_logsRoot))
        {
            Directory.Delete(_logsRoot, recursive: true);
        }
    }

    private void CreateSessionWithNotification(string timestamp, string notificationsContent, string? applicationContent = null)
    {
        var folder = Path.Combine(_logsRoot, $"log_{timestamp}.something");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notifications.log"), notificationsContent);
        if (applicationContent is not null)
        {
            File.WriteAllText(Path.Combine(folder, "application.log"), applicationContent);
        }
    }

    private static string ChatMessageLine(string taskId, int typeCode) =>
        "2026-08-08 14:30:05.123 +00:00|Got notification | ChatMessageReceived\n" +
        "{\n\"message\": {\"type\": " + typeCode + ", \"templateId\": \"" + taskId + " 0\"}\n}\n";

    private static string SessionModeLine(string mode) =>
        "2026-08-08 14:00:00.000 +00:00|application|Session mode: " + mode + "\n";

    [Fact]
    public void ReplayHistory_MissingLogsRoot_ReturnsZeroRatherThanThrowing()
    {
        var watcher = new GameLogWatcher();

        var count = watcher.ReplayHistory(Path.Combine(_logsRoot, "does-not-exist"));

        Assert.Equal(0, count);
    }

    [Fact]
    public void ReplayHistory_ReturnsSessionCountAndFiresEventFromRealFile()
    {
        CreateSessionWithNotification("2026.08.01_10-00-00", ChatMessageLine("task-abc", 10));

        var watcher = new GameLogWatcher();
        var fired = new List<(string TaskId, QuestTaskStatus Status)>();
        watcher.TaskStatusChanged += (_, e) => fired.Add((e.TaskId, e.Status));

        var count = watcher.ReplayHistory(_logsRoot);

        Assert.Equal(1, count);
        var single = Assert.Single(fired);
        Assert.Equal("task-abc", single.TaskId);
        Assert.Equal(QuestTaskStatus.Started, single.Status);
    }

    [Fact]
    public void ReplayHistory_ProcessesMultipleSessionsInChronologicalOrder()
    {
        // Same task: accepted in an older session, then finished in a
        // newer one - a caller tracking "current status per task" (last
        // event wins) must see Finished as the final state, which only
        // holds if replay happens oldest-first.
        CreateSessionWithNotification("2026.08.01_10-00-00", ChatMessageLine("task-abc", 10));
        CreateSessionWithNotification("2026.08.05_10-00-00", ChatMessageLine("task-abc", 12));

        var watcher = new GameLogWatcher();
        var statusHistory = new List<QuestTaskStatus>();
        watcher.TaskStatusChanged += (_, e) => statusHistory.Add(e.Status);

        var count = watcher.ReplayHistory(_logsRoot);

        Assert.Equal(2, count);
        Assert.Equal(new[] { QuestTaskStatus.Started, QuestTaskStatus.Finished }, statusHistory);
    }

    [Fact]
    public void ReplayHistory_SessionWithoutNotificationsLog_SkippedWithoutError()
    {
        var folder = Path.Combine(_logsRoot, "log_2026.08.01_10-00-00.something");
        Directory.CreateDirectory(folder);
        // No notifications.log written.

        var watcher = new GameLogWatcher();

        var count = watcher.ReplayHistory(_logsRoot);

        Assert.Equal(1, count);
    }

    [Fact]
    public void ReplayHistory_TagsEventsWithEachSessionsOwnMode()
    {
        // Older session was PvE, newer session was regular PvP - each
        // session's own application.log must be recovered and used to tag
        // that session's quest events, not whatever mode is currently live.
        CreateSessionWithNotification(
            "2026.08.01_10-00-00",
            ChatMessageLine("task-pve", 10),
            SessionModeLine("Pve"));
        CreateSessionWithNotification(
            "2026.08.05_10-00-00",
            ChatMessageLine("task-pvp", 10),
            SessionModeLine("Pvp"));

        var watcher = new GameLogWatcher();
        var fired = new List<(string TaskId, GameMode Mode)>();
        watcher.TaskStatusChanged += (_, e) => fired.Add((e.TaskId, e.Mode));

        watcher.ReplayHistory(_logsRoot);

        Assert.Equal(2, fired.Count);
        Assert.Equal(("task-pve", GameMode.Pve), fired[0]);
        Assert.Equal(("task-pvp", GameMode.Regular), fired[1]);
    }

    [Fact]
    public void ReplayHistory_DoesNotFireGameModeChangedForHistoricalSessions()
    {
        // GameModeChanged is meant to reflect the CURRENT live session
        // (MainWindow reloads quest data whenever it fires) - replaying a
        // years-old session's "Session mode: ..." line must not trigger
        // that live-facing event.
        CreateSessionWithNotification(
            "2026.08.01_10-00-00",
            ChatMessageLine("task-pve", 10),
            SessionModeLine("Pve"));

        var watcher = new GameLogWatcher();
        var fireCount = 0;
        watcher.GameModeChanged += (_, _) => fireCount++;

        watcher.ReplayHistory(_logsRoot);

        Assert.Equal(0, fireCount);
        // CurrentGameMode is reset to null once replay finishes (even
        // though it was Pve internally during replay, to tag the replayed
        // TaskStatusChanged event correctly) - this is deliberate: it
        // forces the live tailer's first "Session mode: ..." line to
        // always be treated as a genuine change and reported via
        // GameModeChanged, regardless of what mode the last historical
        // session happened to be.
        Assert.Null(watcher.CurrentGameMode);
    }

    // Regression test for a real bug: if the most recently replayed
    // historical session happened to be the same mode as the live session
    // (e.g. both Pve), the live "Session mode: ..." line processed right
    // after ReplayHistory would look like "no change" from
    // _currentGameMode's perspective and never fire GameModeChanged -
    // leaving MainWindow's own mode tracking (which only updates via that
    // event) stuck on its Regular default even while playing PvE. Quest
    // STATUS events still got tagged correctly (they read _currentGameMode
    // directly rather than depending on the event), which is what made this
    // so easy to miss: progress files updated fine, but the task list
    // itself never reloaded for the new mode.
    [Fact]
    public void ReplayHistory_ThenLiveSessionModeLine_StillFiresGameModeChangedEvenIfSameModeAsLastReplayedSession()
    {
        CreateSessionWithNotification(
            "2026.08.01_10-00-00",
            ChatMessageLine("task-pve", 10),
            SessionModeLine("Pve"));

        var watcher = new GameLogWatcher();
        var fireCount = 0;
        watcher.GameModeChanged += (_, _) => fireCount++;

        watcher.ReplayHistory(_logsRoot);
        Assert.Equal(0, fireCount);

        var liveLine = "2026-08-17 22:00:00.000 +00:00|application|Session mode: Pve\n";
        watcher.ProcessChunk(liveLine, isApplicationLog: true);

        Assert.Equal(1, fireCount);
    }
}
