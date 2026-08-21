namespace TarkovHelper.Core.Maps;

// Maps EFT's internal Location: id (from application.log, e.g. "bigmap")
// to tarkov.dev's normalizedName (e.g. "customs") used as the key into
// maps.json calibration data. Verified against SPT server location
// base.json files and tarkov-data-manager's update-maps.mjs (the code that
// populates the GraphQL API's own nameId field from the same BSG data) -
// the log's raw Location: string IS the API's nameId by design.
public static class MapNameResolver
{
    private static readonly Dictionary<string, string> NameIdToNormalizedName = new(StringComparer.Ordinal)
    {
        ["bigmap"] = "customs",
        ["factory4_day"] = "factory",
        ["factory4_night"] = "factory",
        ["Woods"] = "woods",
        ["Shoreline"] = "shoreline",
        ["Interchange"] = "interchange",
        ["RezervBase"] = "reserve",
        ["Lighthouse"] = "lighthouse",
        ["TarkovStreets"] = "streets-of-tarkov",
        ["laboratory"] = "the-lab",
        ["Sandbox"] = "ground-zero",
        ["Sandbox_high"] = "ground-zero",
        ["Labyrinth"] = "the-labyrinth",
    };

    public static string? ToNormalizedName(string nameId) =>
        NameIdToNormalizedName.TryGetValue(nameId, out var normalizedName) ? normalizedName : null;
}
