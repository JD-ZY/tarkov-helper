using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.Logs;

public enum QuestTaskStatus
{
    Started = 10,
    Failed = 11,
    Finished = 12,
}

public class GameModeChangedEventArgs : EventArgs
{
    public required GameMode Mode { get; init; }
}

public class TaskStatusChangedEventArgs : EventArgs
{
    public required string TaskId { get; init; }
    public required QuestTaskStatus Status { get; init; }

    // Which mode this quest event happened in - a PvE character and a PvP
    // character have entirely separate quest progress in-game, so a status
    // change observed while playing one mode must never be applied to the
    // other's tracked progress. Populated from whatever "Session mode: ..."
    // line most recently preceded this event in the same log stream
    // (chronologically true for both live tailing and ReplayHistory, since
    // each session's own application.log is processed before that same
    // session's notifications.log). Defaults to Regular if no mode line was
    // ever seen (e.g. a session log that starts mid-stream).
    public GameMode Mode { get; init; } = GameMode.Regular;
}

public class MapLoadingEventArgs : EventArgs
{
    public required string ScenePath { get; init; }
}

public class MapLoadedEventArgs : EventArgs
{
    public string? MapNameId { get; init; }
    public bool IsOnline { get; init; }
    public string? RaidId { get; init; }
}

public class RaidExitedEventArgs : EventArgs
{
    public string? Location { get; init; }
    public string? RaidId { get; init; }
}
