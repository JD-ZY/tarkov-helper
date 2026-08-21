using TarkovHelper.Core.Logs;

namespace TarkovHelper.Core.Tests;

public class GameLogWatcherTests
{
    private const string TimestampPrefix = "2026-08-08 14:30:05.123 +00:00|";

    [Fact]
    public void SessionModePve_FiresGameModeChangedAndUpdatesCurrentGameMode()
    {
        var watcher = new GameLogWatcher();
        GameModeChangedEventArgs? received = null;
        watcher.GameModeChanged += (_, e) => received = e;

        var line = TimestampPrefix + "application|Session mode: Pve\n";
        watcher.ProcessChunk(line, isApplicationLog: true);

        Assert.NotNull(received);
        Assert.Equal(Models.GameMode.Pve, received!.Mode);
        Assert.Equal(Models.GameMode.Pve, watcher.CurrentGameMode);
    }

    [Fact]
    public void SessionModePvpSeason_MapsToRegularGameMode()
    {
        var watcher = new GameLogWatcher();
        GameModeChangedEventArgs? received = null;
        watcher.GameModeChanged += (_, e) => received = e;

        var line = TimestampPrefix + "application|Session mode: PvpSeason\n";
        watcher.ProcessChunk(line, isApplicationLog: true);

        Assert.NotNull(received);
        Assert.Equal(Models.GameMode.Regular, received!.Mode);
    }

    [Fact]
    public void SessionModeUnchanged_DoesNotRefireGameModeChanged()
    {
        var watcher = new GameLogWatcher();
        var fireCount = 0;
        watcher.GameModeChanged += (_, _) => fireCount++;

        var lines = TimestampPrefix + "application|Session mode: Pve\n" +
                    TimestampPrefix + "application|Session mode: Pve\n";
        watcher.ProcessChunk(lines, isApplicationLog: true);

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void SceneLoadingLine_FiresMapLoadingEvent()
    {
        var watcher = new GameLogWatcher();
        MapLoadingEventArgs? received = null;
        watcher.MapLoading += (_, e) => received = e;

        var line = TimestampPrefix + "application|scene preset path:maps/factory_day.bundle rcid:factory4_day.scenespreset.asset\n";
        watcher.ProcessChunk(line, isApplicationLog: true);

        Assert.NotNull(received);
        Assert.Equal("maps/factory_day.bundle", received!.ScenePath);
    }

    // Verified against a real live PvE application.log: PvE (and, by the
    // same code path, Practice) raids never emit the "TRACE-NetworkGameCreate
    // profileStatus, Location: ..., RaidMode: ..., shortId: ..." line at
    // all - they log "TRACE-NetworkGameMatching" instead. Without this
    // fallback, MapLoaded (and therefore the whole map window) never
    // updates for PvE/Practice raids. GameStarted does still fire in that
    // real log, so it's used as the trigger, with rcid (captured off the
    // earlier scene-preset line) supplying the map id TRACE-NetworkGameCreate
    // would otherwise have provided.
    [Fact]
    public void SceneLoadingThenGameStarted_WithoutNetworkGameCreate_FiresMapLoadedFromRcid()
    {
        var watcher = new GameLogWatcher();
        MapLoadedEventArgs? received = null;
        watcher.MapLoaded += (_, e) => received = e;

        var lines = TimestampPrefix + "application|scene preset path:maps/customs_preset.bundle rcid:bigmap.scenespreset.asset\n" +
                    TimestampPrefix + "application|GameStarted\n";
        watcher.ProcessChunk(lines, isApplicationLog: true);

        Assert.NotNull(received);
        Assert.Equal("bigmap", received!.MapNameId);
    }

    [Fact]
    public void SceneLoadingThenNetworkGameCreateThenGameStarted_DoesNotFireMapLoadedTwice()
    {
        var watcher = new GameLogWatcher();
        var fireCount = 0;
        watcher.MapLoaded += (_, _) => fireCount++;

        var lines = TimestampPrefix + "application|scene preset path:maps/customs_preset.bundle rcid:bigmap.scenespreset.asset\n" +
                    TimestampPrefix + "application|TRACE-NetworkGameCreate profileStatus, Location: bigmap, RaidMode: Online, shortId: A1B2C3\n" +
                    TimestampPrefix + "application|GameStarted\n";
        watcher.ProcessChunk(lines, isApplicationLog: true);

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void MatchFoundLine_FiresMapLoadedEventWithParsedFields()
    {
        var watcher = new GameLogWatcher();
        MapLoadedEventArgs? received = null;
        watcher.MapLoaded += (_, e) => received = e;

        var line = TimestampPrefix +
            "application|TRACE-NetworkGameCreate profileStatus, Location: bigmap, RaidMode: Online, shortId: A1B2C3\n";
        watcher.ProcessChunk(line, isApplicationLog: true);

        Assert.NotNull(received);
        Assert.Equal("bigmap", received!.MapNameId);
        Assert.True(received.IsOnline);
        Assert.Equal("A1B2C3", received.RaidId);
    }

    [Fact]
    public void OfflineRaid_IsOnlineIsFalse()
    {
        var watcher = new GameLogWatcher();
        MapLoadedEventArgs? received = null;
        watcher.MapLoaded += (_, e) => received = e;

        var line = TimestampPrefix +
            "application|TRACE-NetworkGameCreate profileStatus, Location: factory4_day, RaidMode: Offline, shortId: X9Y8Z7\n";
        watcher.ProcessChunk(line, isApplicationLog: true);

        Assert.NotNull(received);
        Assert.False(received!.IsOnline);
    }

    [Fact]
    public void GameStartingLine_FiresRaidStarting()
    {
        var watcher = new GameLogWatcher();
        var fired = false;
        watcher.RaidStarting += (_, _) => fired = true;

        watcher.ProcessChunk(TimestampPrefix + "application|GameStarting\n", isApplicationLog: true);

        Assert.True(fired);
    }

    [Fact]
    public void GameStartedLine_FiresRaidStarted()
    {
        var watcher = new GameLogWatcher();
        var fired = false;
        watcher.RaidStarted += (_, _) => fired = true;

        watcher.ProcessChunk(TimestampPrefix + "application|GameStarted\n", isApplicationLog: true);

        Assert.True(fired);
    }

    [Fact]
    public void UserMatchOverNotification_FiresRaidExitedWithLocationAndRaidId()
    {
        var watcher = new GameLogWatcher();
        RaidExitedEventArgs? received = null;
        watcher.RaidExited += (_, e) => received = e;

        var line = TimestampPrefix + "Got notification | UserMatchOver\n" +
                   "{\n\"location\": \"bigmap\",\n\"shortId\": \"A1B2C3\"\n}\n";
        watcher.ProcessChunk(line, isApplicationLog: false);

        Assert.NotNull(received);
        Assert.Equal("bigmap", received!.Location);
        Assert.Equal("A1B2C3", received.RaidId);
    }

    [Theory]
    [InlineData(10, QuestTaskStatus.Started)]
    [InlineData(11, QuestTaskStatus.Failed)]
    [InlineData(12, QuestTaskStatus.Finished)]
    public void ChatMessageReceived_TaskStatusRange_FiresTaskStatusChanged(int typeCode, QuestTaskStatus expectedStatus)
    {
        var watcher = new GameLogWatcher();
        TaskStatusChangedEventArgs? received = null;
        watcher.TaskStatusChanged += (_, e) => received = e;

        // The json capture group is (?<json>^\{[\s\S]+?^\})? - both the '{'
        // and matching '}' must be alone at the start of a line, with no
        // OTHER line-start '}' occurring first. Single-line JSON never
        // matches (its '}' isn't at a line start) and JSON pretty-printed
        // with a nested object's '}' alone on its own line truncates early.
        // This is inherited as-is from TarkovMonitor's own logPattern, so it
        // only works when the outermost '}' is the first line-start '}' -
        // i.e. nested object closes share a line with something else, as
        // this fixture does.
        var line = TimestampPrefix + "Got notification | ChatMessageReceived\n" +
                   "{\n\"message\": {\"type\": " + typeCode + ", \"templateId\": \"5ac3b93586f77468d543d1a4 0\"}\n}\n";
        watcher.ProcessChunk(line, isApplicationLog: false);

        Assert.NotNull(received);
        Assert.Equal("5ac3b93586f77468d543d1a4", received!.TaskId);
        Assert.Equal(expectedStatus, received.Status);
    }

    [Fact]
    public void ChatMessageReceived_NonTaskType_DoesNotFireTaskStatusChanged()
    {
        var watcher = new GameLogWatcher();
        var fired = false;
        watcher.TaskStatusChanged += (_, _) => fired = true;

        // type 1 = PlayerMessage, outside the [10,12] task-status range.
        var line = TimestampPrefix + "Got notification | ChatMessageReceived\n" +
                   "{\n\"message\": {\"type\": 1, \"templateId\": \"abc 0\"}\n}\n";
        watcher.ProcessChunk(line, isApplicationLog: false);

        Assert.False(fired);
    }

    [Fact]
    public void TemplateIdWithMultipleSpaceTokens_TakesFirstTokenAsTaskId()
    {
        var watcher = new GameLogWatcher();
        TaskStatusChangedEventArgs? received = null;
        watcher.TaskStatusChanged += (_, e) => received = e;

        var line = TimestampPrefix + "Got notification | ChatMessageReceived\n" +
                   "{\n\"message\": {\"type\": 12, \"templateId\": \"questGuid123 0\"}\n}\n";
        watcher.ProcessChunk(line, isApplicationLog: false);

        Assert.Equal("questGuid123", received!.TaskId);
    }

    [Fact]
    public void MultipleLinesInOneChunk_AllProcessed()
    {
        var watcher = new GameLogWatcher();
        var startingFired = false;
        var startedFired = false;
        watcher.RaidStarting += (_, _) => startingFired = true;
        watcher.RaidStarted += (_, _) => startedFired = true;

        var chunk = TimestampPrefix + "application|GameStarting\n" +
                    TimestampPrefix + "application|GameStarted\n";
        watcher.ProcessChunk(chunk, isApplicationLog: true);

        Assert.True(startingFired);
        Assert.True(startedFired);
    }

    [Fact]
    public void UnrelatedApplicationLine_DoesNotThrowOrFireAnyEvent()
    {
        var watcher = new GameLogWatcher();
        var anyFired = false;
        watcher.MapLoading += (_, _) => anyFired = true;
        watcher.MapLoaded += (_, _) => anyFired = true;
        watcher.RaidStarting += (_, _) => anyFired = true;
        watcher.RaidStarted += (_, _) => anyFired = true;

        watcher.ProcessChunk(TimestampPrefix + "application|SomeUnrelatedTraceLine value=42\n", isApplicationLog: true);

        Assert.False(anyFired);
    }
}
