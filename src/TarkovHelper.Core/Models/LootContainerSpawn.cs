namespace TarkovHelper.Core.Models;

// A single physical container spawn point on a map (e.g. one specific safe,
// one specific weapon box) - static per-map, unlike loose-loot spawns whose
// actual item is randomized each raid from a large pool. ContainerType is
// shared across many spawn points (e.g. "safe" appears dozens of times per
// map), so it's kept separate from the per-point position.
public class LootContainerSpawn
{
    public required string ContainerNormalizedName { get; init; }
    public required string ContainerName { get; init; }
    public required float X { get; init; }
    public required float Z { get; init; }
}

// Static value ranking by container type - not live pricing, since a
// container instance's actual contents are randomized per raid and this API
// doesn't expose per-instance loot tables. Ranking reflects which container
// types are worth detouring for in practice (safes/weapon crates historically
// high value, wooden crates/drawers low), matching tarkov.dev's own loot-tier
// presentation approach.
public enum LootTier
{
    Low,
    Medium,
    High,
}

public static class LootContainerTiers
{
    private static readonly Dictionary<string, LootTier> TierByNormalizedName = new()
    {
        ["safe"] = LootTier.High,
        ["bank-safe"] = LootTier.High,
        ["weapon-box"] = LootTier.High,
        ["duffle-bag"] = LootTier.High,
        ["pc-block"] = LootTier.High,
        ["bank-cash-register"] = LootTier.High,
        ["shturmans-stash"] = LootTier.High,

        ["jacket"] = LootTier.Medium,
        ["dead-scav"] = LootTier.Medium,
        ["scav-body"] = LootTier.Medium,
        ["pmc-body"] = LootTier.Medium,
        ["civilian-body"] = LootTier.Medium,
        ["lab-technician-body"] = LootTier.Medium,
        ["medbag-smu06"] = LootTier.Medium,
        ["medcase"] = LootTier.Medium,
        ["medical-supply-crate"] = LootTier.Medium,
        ["technical-supply-crate"] = LootTier.Medium,
        ["plastic-suitcase"] = LootTier.Medium,
        ["cash-register"] = LootTier.Medium,
        ["ground-cache"] = LootTier.Medium,
        ["buried-barrel-cache"] = LootTier.Medium,

        ["wooden-crate"] = LootTier.Low,
        ["wooden-ammo-box"] = LootTier.Low,
        ["grenade-box"] = LootTier.Low,
        ["toolbox"] = LootTier.Low,
        ["drawer"] = LootTier.Low,
        ["ration-supply-crate"] = LootTier.Low,
    };

    // Unrecognized container types (new additions to the game we haven't
    // categorized yet) default to Medium rather than being hidden or
    // mis-ranked at an extreme.
    public static LootTier GetTier(string containerNormalizedName) =>
        TierByNormalizedName.GetValueOrDefault(containerNormalizedName, LootTier.Medium);
}
