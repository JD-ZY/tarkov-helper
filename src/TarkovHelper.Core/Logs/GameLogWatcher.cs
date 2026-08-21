using System.Text.Json;
using System.Text.RegularExpressions;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.Logs;

// Watches EFT's application.log and notifications.log for the raid/quest
// status transitions TarkovMonitor exposes. Per verified research, the
// game's own logs only surface whole-task status transitions (accepted /
// failed / finished) via notifications.log's ChatMessageReceived JSON
// payload - there is no per-objective progress event.
public sealed class GameLogWatcher : IDisposable
{
    // Verbatim from TarkovMonitor's logPattern. The json group matches
    // non-greedily up to the first line starting with '}', so it silently
    // truncates if the payload is pretty-printed with a nested object's
    // closing brace alone on its own line before the real end. TarkovMonitor
    // parses UserMatchOver/ChatMessageReceived JSON with this same pattern
    // in production, so EFT's actual log JSON must be flat/single-line for
    // these events - if that ever changes, this regex needs revisiting.
    private static readonly Regex LogLinePattern = new(
        @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3})(?<tzoffset> [+-]\d{2}:\d{2})?\|(?<message>.+$)\s*(?<json>^\{[\s\S]+?^\})?",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ScenePathPattern = new(
        @"scene preset path:(?<scenePath>maps\/[a-zA-Z0-9_]+\.bundle) rcid:(?<rcid>[a-zA-Z0-9_]+)\.scenespreset\.asset",
        RegexOptions.Compiled);

    private static readonly Regex MatchFoundLocationPattern = new(
        @"Location: (?<map>[^,]+)", RegexOptions.Compiled);

    private static readonly Regex MatchFoundRaidIdPattern = new(
        @"shortId: (?<raidId>[A-Z0-9]{6})", RegexOptions.Compiled);

    private LogFileTailer? _applicationTailer;
    private LogFileTailer? _notificationsTailer;

    // Set from the "scene preset path:...rcid:<id>.scenespreset.asset" line
    // and cleared once MapLoaded actually fires for it - lets PvE/Practice
    // raids (verified against a real PvE application.log: the whole
    // "TRACE-NetworkGameCreate profileStatus, Location: ..., RaidMode: ...
    // shortId: ..." line that regular PvP raids emit is simply absent -
    // PvE logs "TRACE-NetworkGameMatching" instead and never emits that
    // line at all) still get a MapLoaded event, using rcid as the map
    // nameId once GameStarted confirms the raid actually began.
    private string? _pendingMapNameId;
    private bool _mapLoadedFiredForPendingMap;
    private GameMode? _currentGameMode;

    // True only while ReplayHistory is processing a past session's
    // application.log to recover that session's mode (see ReplayHistory).
    // Suppresses MapLoading/MapLoaded/RaidStarting/RaidStarted/
    // GameModeChanged during replay - those are meant to reflect what's
    // happening in the CURRENT live session, and every subscriber
    // (MainWindow's raid-status text, the map window, the quest-reload-on-
    // mode-change handler) is already wired up before ReplayHistory runs.
    // Firing them for years-old historical sessions would spam stale map/
    // raid state and trigger spurious quest reloads before the real live
    // session is even attached. TaskStatusChanged is NOT suppressed - quest
    // history replay is the entire point of this method.
    private bool _isReplaying;

    public event EventHandler<MapLoadingEventArgs>? MapLoading;
    public event EventHandler<MapLoadedEventArgs>? MapLoaded;
    public event EventHandler? RaidStarting;
    public event EventHandler? RaidStarted;
    public event EventHandler<RaidExitedEventArgs>? RaidExited;
    public event EventHandler<TaskStatusChangedEventArgs>? TaskStatusChanged;
    public event EventHandler<GameModeChangedEventArgs>? GameModeChanged;

    // The most recently observed session mode, or null if no "Session
    // mode: ..." line has been seen yet this run (e.g. attaching mid-raid
    // to a log that already scrolled past it). Exposed so a caller that
    // starts watching after the fact - or wants an initial value before the
    // first live line arrives - isn't stuck with no mode information.
    public GameMode? CurrentGameMode => _currentGameMode;

    // Scans every retained session log folder (oldest to newest) and
    // replays TaskStatusChanged events synchronously, so callers can
    // bootstrap the player's true current quest state (active/completed)
    // before live watching begins - EFT keeps no separate save file for
    // this, only rolling per-session logs, so history has to be recovered
    // by re-reading old sessions (same approach as TarkovMonitor's "read
    // past logs" feature). Returns the number of session folders scanned.
    public int ReplayHistory(string? logsRootOverride = null)
    {
        var logsRoot = logsRootOverride ?? LogPathResolver.GetDefaultLogsFolder();
        if (logsRoot is null)
        {
            return 0;
        }

        var sessions = LogPathResolver.GetAllSessionFoldersChronological(logsRoot);
        _isReplaying = true;
        try
        {
            foreach (var (folder, _) in sessions)
            {
                // Each session folder has its own application.log, and each
                // session can genuinely be a different mode than the last
                // (the player can queue PvE one raid and PvP the next) -
                // process this session's application.log FIRST so
                // _currentGameMode reflects that session's actual mode
                // before its notifications.log's quest events are replayed
                // below and tagged with it. Without this, ALL replayed
                // history would be tagged with whichever mode happened to
                // be live when Start() last set _currentGameMode (or
                // Regular, if none ever had), silently misattributing PvE
                // quest progress to PvP or vice versa for every session
                // except whichever is currently live.
                var applicationLog = FindLogFile(folder, "application.log");
                if (applicationLog is not null)
                {
                    try
                    {
                        var appText = ReadAllTextShared(applicationLog);
                        ProcessChunk(appText, isApplicationLog: true);
                    }
                    catch (IOException)
                    {
                        // File may be locked/mid-write for the current session; skip it.
                    }
                }

                var notificationsLog = FindLogFile(folder, "notifications.log");
                if (notificationsLog is null)
                {
                    continue;
                }

                try
                {
                    var text = ReadAllTextShared(notificationsLog);
                    ProcessChunk(text, isApplicationLog: false);
                }
                catch (IOException)
                {
                    // File may be locked/mid-write for the current session; skip it.
                }
            }
        }
        finally
        {
            // Replay walks historical sessions and, as a side effect of
            // recovering each one's mode to tag its quest events correctly,
            // leaves _currentGameMode set to whatever the MOST RECENT past
            // session's mode was. If that happens to already equal the
            // current live session's mode, the live "Session mode: ..."
            // line processed once Start() begins tailing would look like
            // "no change" and never fire GameModeChanged - so MainWindow's
            // _currentGameMode (which starts at Regular and only updates
            // via that event) would never learn the real mode, silently
            // leaving quest data on the wrong mode all session. Resetting
            // to null here forces the live tailer's first mode line, always,
            // to be treated as a genuine change and reported.
            _currentGameMode = null;
            _isReplaying = false;
        }

        return sessions.Count;
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public bool Start()
    {
        var logsRoot = LogPathResolver.GetDefaultLogsFolder();
        if (logsRoot is null)
        {
            return false;
        }

        var sessionFolder = LogPathResolver.GetLatestSessionFolder(logsRoot);
        if (sessionFolder is null)
        {
            return false;
        }

        var applicationLog = FindLogFile(sessionFolder, "application.log");
        var notificationsLog = FindLogFile(sessionFolder, "notifications.log");

        if (applicationLog is not null)
        {
            // application.log is read from the start (skipExistingContent:
            // false) to recover current profile/raid state on attach.
            _applicationTailer = new LogFileTailer(applicationLog, TimeSpan.FromSeconds(5), skipExistingContent: false);
            _applicationTailer.NewLogData += (_, text) => ProcessChunk(text, isApplicationLog: true);
            _applicationTailer.Start();
        }

        if (notificationsLog is not null)
        {
            _notificationsTailer = new LogFileTailer(notificationsLog, TimeSpan.FromSeconds(5), skipExistingContent: true);
            _notificationsTailer.NewLogData += (_, text) => ProcessChunk(text, isApplicationLog: false);
            _notificationsTailer.Start();
        }

        return _applicationTailer is not null || _notificationsTailer is not null;
    }

    private static string? FindLogFile(string sessionFolder, string suffix)
    {
        return Directory.EnumerateFiles(sessionFolder, "*.log", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).Contains(suffix.Replace(".log", "")));
    }

    // Internal (not private) so tests can feed synthetic log text directly
    // without needing real files on disk or EFT installed.
    internal void ProcessChunk(string text, bool isApplicationLog)
    {
        foreach (Match match in LogLinePattern.Matches(text))
        {
            var message = match.Groups["message"].Value;
            var json = match.Groups["json"].Success ? match.Groups["json"].Value : null;

            if (isApplicationLog)
            {
                HandleApplicationLine(message);
            }
            else
            {
                HandleNotificationLine(message, json);
            }
        }
    }

    private void HandleApplicationLine(string message)
    {
        // Verified against a real application.log: EFT logs a line like
        // "application|Session mode: Pve" (also seen: "PvpSeason") once per
        // matchmaking/backend attach - well before the raid's own map/mode
        // lines, and it fires again on returning to the menu and picking a
        // different mode, so this is the one live signal that tracks mode
        // switches within a single running game session without waiting
        // for a raid to actually start. Substring match rather than a strict
        // "is one of these exact values" regex, since only "does it contain
        // Pve" needs to be distinguished - permanent PvP and seasonal PvP
        // both map to the same tarkov.dev gameMode (regular) anyway.
        if (message.Contains("application|Session mode: "))
        {
            var mode = message.Contains("Session mode: Pve", StringComparison.Ordinal)
                ? GameMode.Pve
                : GameMode.Regular;

            if (_currentGameMode != mode)
            {
                _currentGameMode = mode;
                if (!_isReplaying)
                {
                    GameModeChanged?.Invoke(this, new GameModeChangedEventArgs { Mode = mode });
                }
            }

            return;
        }

        if (message.Contains("application|scene preset path:"))
        {
            var m = ScenePathPattern.Match(message);
            if (m.Success)
            {
                if (!_isReplaying)
                {
                    MapLoading?.Invoke(this, new MapLoadingEventArgs { ScenePath = m.Groups["scenePath"].Value });
                }
                _pendingMapNameId = m.Groups["rcid"].Value;
                _mapLoadedFiredForPendingMap = false;
            }
            return;
        }

        if (message.Contains("application|TRACE-NetworkGameCreate profileStatus"))
        {
            var locationMatch = MatchFoundLocationPattern.Match(message);
            var raidIdMatch = MatchFoundRaidIdPattern.Match(message);
            var isOnline = message.Contains("RaidMode: Online");

            _mapLoadedFiredForPendingMap = true;
            if (!_isReplaying)
            {
                MapLoaded?.Invoke(this, new MapLoadedEventArgs
                {
                    MapNameId = locationMatch.Success ? locationMatch.Groups["map"].Value : _pendingMapNameId,
                    IsOnline = isOnline,
                    RaidId = raidIdMatch.Success ? raidIdMatch.Groups["raidId"].Value : null,
                });
            }
            return;
        }

        if (message.Contains("application|GameStarting"))
        {
            if (!_isReplaying)
            {
                RaidStarting?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        if (message.Contains("application|GameStarted"))
        {
            // PvE/Practice raids never emit TRACE-NetworkGameCreate, so
            // MapLoaded would otherwise never fire for them - GameStarted
            // always does fire (verified in the real PvE log), so fall
            // back to it here using the rcid captured off the scene-preset
            // line. IsOnline/RaidId aren't derivable from this line alone
            // (PvE and Practice both skip TRACE-NetworkGameCreate, so
            // there's no way to tell them apart here) - default IsOnline
            // false rather than assert a value that's only correct half
            // the time; MapNameId (the part the map window actually reads)
            // is still correct.
            if (!_mapLoadedFiredForPendingMap && _pendingMapNameId is not null)
            {
                if (!_isReplaying)
                {
                    MapLoaded?.Invoke(this, new MapLoadedEventArgs
                    {
                        MapNameId = _pendingMapNameId,
                        IsOnline = false,
                        RaidId = null,
                    });
                }
                _mapLoadedFiredForPendingMap = true;
            }

            if (!_isReplaying)
            {
                RaidStarted?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void HandleNotificationLine(string message, string? json)
    {
        if (json is null)
        {
            return;
        }

        if (message.Contains("Got notification | UserMatchOver"))
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            RaidExited?.Invoke(this, new RaidExitedEventArgs
            {
                Location = root.TryGetProperty("location", out var loc) ? loc.GetString() : null,
                RaidId = root.TryGetProperty("shortId", out var raidId) ? raidId.GetString() : null,
            });
            return;
        }

        if (!message.Contains("Got notification | ChatMessageReceived"))
        {
            return;
        }

        using var chatDoc = JsonDocument.Parse(json);
        if (!chatDoc.RootElement.TryGetProperty("message", out var msgElement))
        {
            return;
        }

        if (!msgElement.TryGetProperty("type", out var typeElement) || !typeElement.TryGetInt32(out var typeValue))
        {
            return;
        }

        // Quest status transitions occupy the range [Started=10, Finished=12].
        if (typeValue < (int)QuestTaskStatus.Started || typeValue > (int)QuestTaskStatus.Finished)
        {
            return;
        }

        if (!msgElement.TryGetProperty("templateId", out var templateIdElement))
        {
            return;
        }

        var templateId = templateIdElement.GetString();
        if (string.IsNullOrEmpty(templateId))
        {
            return;
        }

        // templateId is "<questGuid> <index>", space-delimited.
        var taskId = templateId.Split(' ')[0];

        TaskStatusChanged?.Invoke(this, new TaskStatusChangedEventArgs
        {
            TaskId = taskId,
            Status = (QuestTaskStatus)typeValue,
            Mode = _currentGameMode ?? GameMode.Regular,
        });
    }

    public void Dispose()
    {
        _applicationTailer?.Dispose();
        _notificationsTailer?.Dispose();
    }
}
