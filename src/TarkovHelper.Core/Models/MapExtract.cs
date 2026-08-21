namespace TarkovHelper.Core.Models;

public class MapExtract
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // "pmc", "scav", or "shared" - verified exhaustive against live data.
    public string Faction { get; set; } = string.Empty;

    public float X { get; set; }
    public float Z { get; set; }
}
