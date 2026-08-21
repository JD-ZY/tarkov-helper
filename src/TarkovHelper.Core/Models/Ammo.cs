namespace TarkovHelper.Core.Models;

// A single trader's buy offer for an ammo item - distinct from ItemDetails'
// TraderOffer (which represents SELLING an item to a trader, single best
// offer only): ammo needs every trader that BUYS it, each with its own
// loyalty-level gate and optional quest prerequisite, not just the best price.
public class AmmoTraderOffer
{
    public required string TraderName { get; init; }
    public required int LoyaltyLevel { get; init; }
    public required int PriceRub { get; init; }

    // Name of the quest that must be completed to unlock this trader offer,
    // if any - null for offers with no quest prerequisite (most of them).
    // tarkov.dev's schema field is TraderOffer.taskUnlock: Task, verified
    // against the-hideout/tarkov-api's schema-static.mjs.
    public string? RequiredQuestName { get; init; }
}

// Ballistic/purchase data for a single ammo round, sourced from tarkov.dev's
// dedicated `ammo` GraphQL query (type Ammo) - field names verified against
// the-hideout/tarkov-api's schema-static.mjs, not guessed: damage,
// armorDamage, penetrationPower, initialSpeed (not "velocity"),
// accuracyModifier/recoilModifier (not the deprecated accuracy/recoil).
public class Ammo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }

    // Free-form string from tarkov.dev, e.g. "Caliber556x45NATO" - used to
    // group ammo in the UI. Not an enum on their side, so treated as an
    // opaque grouping key here rather than parsed into a strong type.
    public required string Caliber { get; init; }

    public required int Damage { get; init; }
    public required int ArmorDamage { get; init; }
    public required int PenetrationPower { get; init; }
    public required double FragmentationChance { get; init; }
    public required double RicochetChance { get; init; }

    // Muzzle velocity in m/s - tarkov.dev's field is literally named
    // initialSpeed, not velocity; kept as InitialSpeed here so a future
    // reader cross-checking against the API isn't confused by a renamed
    // field with no obvious source.
    public required double InitialSpeed { get; init; }

    public double? AccuracyModifier { get; init; }
    public double? RecoilModifier { get; init; }

    public required bool Tracer { get; init; }

    public List<AmmoTraderOffer> TraderOffers { get; init; } = new();

    // Lowest RUB price across all trader offers, ignoring loyalty-level/
    // quest gating - a quick "what's the cheapest source" signal for
    // sorting/display, distinct from BestTraderOffer's per-offer detail.
    public int? CheapestPriceRub => TraderOffers.Count > 0 ? TraderOffers.Min(o => o.PriceRub) : null;
}
