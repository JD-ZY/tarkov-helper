using TarkovHelper.Core.Models;

namespace TarkovHelper.App;

public class HideoutStationRow
{
    public HideoutStationRow(HideoutStation station)
    {
        Station = station;
    }

    public HideoutStation Station { get; }

    public string Name => Station.Name;
    public int MaxLevel => Station.Levels.Count > 0 ? Station.Levels.Max(l => l.Level) : 0;

    public int CurrentLevel
    {
        get => Station.CurrentLevel;
        set => Station.CurrentLevel = Math.Clamp(value, 0, MaxLevel);
    }

    public string NextLevelStatus => CurrentLevel >= MaxLevel
        ? "Max level"
        : $"Level {CurrentLevel + 1} of {MaxLevel}";

    public string NextLevelRequirements
    {
        get
        {
            var next = Station.Levels.FirstOrDefault(l => l.Level == CurrentLevel + 1);
            if (next is null)
            {
                return string.Empty;
            }

            var parts = new List<string>();

            if (next.ItemRequirements.Count > 0)
            {
                parts.Add(string.Join(", ", next.ItemRequirements.Select(r =>
                    $"{r.Count}x {r.Item.Name}" + (r.FoundInRaid ? " (FIR)" : string.Empty))));
            }

            if (next.StationRequirements.Count > 0)
            {
                parts.Add(string.Join(", ", next.StationRequirements.Select(r =>
                    $"{r.StationName ?? r.StationId} lvl {r.Level}")));
            }

            if (next.TraderRequirements.Count > 0)
            {
                parts.Add(string.Join(", ", next.TraderRequirements.Select(r =>
                    $"{r.TraderName} lvl {r.Level}")));
            }

            return string.Join(" | ", parts);
        }
    }
}
