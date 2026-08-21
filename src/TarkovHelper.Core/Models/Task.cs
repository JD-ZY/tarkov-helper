namespace TarkovHelper.Core.Models;

// tarkov.dev's GraphQL/JSON APIs only distinguish "regular" (both permanent
// PvP and seasonal PvP share the same task pool/data) vs "pve" - there is no
// third API-side mode even though the game itself has three session kinds
// (Pvp, PvpSeason, Pve, verified against a real application.log's "Session
// mode: ..." lines). Modeled as a two-value enum rather than mirroring the
// game's three session kinds, since nothing downstream needs to tell
// permanent PvP and seasonal PvP apart.
public enum GameMode
{
    Regular,
    Pve,
}

public class Trader
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class GameMap
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
}

public class Item
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ShortName { get; set; }
}

// An exact in-raid location for an objective, when the game data provides
// one - not every objective has this (e.g. "hand item to trader" doesn't
// happen in-raid at all), only ones tied to a physical spot on a map.
public class ObjectiveZone
{
    public string? MapNormalizedName { get; set; }
    public float X { get; set; }
    public float Z { get; set; }
}

// TaskObjective is a GraphQL interface; only fields common to every
// implementing type plus TaskObjectiveItem's fields (via inline fragment)
// are modeled here, since "what items do I need" only applies to that one
// objective type. Other type-specific fields (targetNames, etc.) aren't
// needed yet.
public class TaskObjective
{
    public string? Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<GameMap> Maps { get; set; } = new();
    public bool Optional { get; set; }

    // Populated only when Type == "giveItem"/"findItem" (TaskObjectiveItem).
    public List<Item> Items { get; set; } = new();
    public int? Count { get; set; }
    public bool? FoundInRaid { get; set; }

    // Exact in-raid position(s), when available - most objectives
    // reference a map (see Maps) but not all have a precise spot.
    public List<ObjectiveZone> Zones { get; set; } = new();
}

public class TaskStatusRequirement
{
    public TaskRef Task { get; set; } = new();
    public List<string> Status { get; set; } = new();
}

public class TaskRef
{
    public string? Id { get; set; }
}

public class QuestTask
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public Trader Trader { get; set; } = new();

    // Singular on Task (verified against schema-static.mjs), unlike
    // TaskObjective.Maps which is plural.
    public GameMap? Map { get; set; }

    public int? MinPlayerLevel { get; set; }
    public string? WikiLink { get; set; }
    public bool? KappaRequired { get; set; }
    public List<TaskStatusRequirement> TaskRequirements { get; set; } = new();
    public List<TaskObjective> Objectives { get; set; } = new();

    // Local-only tracking state, not from the API.
    public bool IsComplete { get; set; }
    public bool IsActive { get; set; }
}
