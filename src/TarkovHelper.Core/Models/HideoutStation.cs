namespace TarkovHelper.Core.Models;

public class HideoutItemRequirement
{
    public Item Item { get; set; } = new();
    public int Count { get; set; }
    public bool FoundInRaid { get; set; }
}

public class HideoutStationRequirement
{
    public string? StationId { get; set; }
    public string? StationName { get; set; }
    public int Level { get; set; }
}

public class HideoutTraderRequirement
{
    public string TraderName { get; set; } = string.Empty;
    public int Level { get; set; }
}

public class HideoutLevel
{
    public int Level { get; set; }
    public List<HideoutItemRequirement> ItemRequirements { get; set; } = new();
    public List<HideoutStationRequirement> StationRequirements { get; set; } = new();
    public List<HideoutTraderRequirement> TraderRequirements { get; set; } = new();
}

public class HideoutStation
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public List<HideoutLevel> Levels { get; set; } = new();

    // Local-only tracking state, not from the API.
    public int CurrentLevel { get; set; }
}
