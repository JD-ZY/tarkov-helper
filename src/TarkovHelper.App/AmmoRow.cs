using TarkovHelper.Core.Models;

namespace TarkovHelper.App;

public class AmmoRow
{
    public AmmoRow(Ammo ammo)
    {
        AmmoData = ammo;
    }

    public Ammo AmmoData { get; }

    public string Name => AmmoData.ShortName;
    public string Caliber => FormatCaliber(AmmoData.Caliber);
    public int Damage => AmmoData.Damage;
    public int ArmorDamage => AmmoData.ArmorDamage;
    public int PenetrationPower => AmmoData.PenetrationPower;
    public double InitialSpeed => AmmoData.InitialSpeed;
    public string Tracer => AmmoData.Tracer ? "Yes" : "";

    // Traders sold lowest-price-first, so the cheapest legitimate source is
    // always first in the string even when several traders carry the same
    // round - matches how a player would actually want to scan this column
    // (where's it cheapest, and what do I need to unlock that).
    public string TraderOffers
    {
        get
        {
            if (AmmoData.TraderOffers.Count == 0)
            {
                return "Not sold by any trader";
            }

            var parts = AmmoData.TraderOffers
                .OrderBy(o => o.PriceRub)
                .Select(o =>
                {
                    var text = $"{o.TraderName} LL{o.LoyaltyLevel} ({o.PriceRub}₽)";
                    return o.RequiredQuestName is not null
                        ? $"{text} [requires \"{o.RequiredQuestName}\"]"
                        : text;
                });

            return string.Join(", ", parts);
        }
    }

    // tarkov.dev's caliber field is a raw enum-like identifier
    // ("Caliber556x45NATO", "Caliber12g", "Caliber1143x23ACP") rather than a
    // display string. Real bug this simple approach fixes: an earlier
    // version tried to algorithmically reformat these into conventional
    // notation (splitting digits from letters, inserting a decimal point)
    // - verified directly against the real, complete list of calibers from
    // a live fetch, and it produced actively wrong output across most of
    // them (e.g. "Caliber1143x23ACP" - the real .45 ACP round - became
    // "1.143 x23ACP" instead of anything resembling "11.43x23mm ACP";
    // "Caliber12g" - 12 gauge shotgun - became "12 g"). The identifier
    // isn't actually decimal-point-shifted in a uniform way (compare
    // "556"->5.56mm against "1143"->11.43mm - different digit-shift rules
    // for different calibers), so no single splitting rule reformats all
    // of them correctly. Stripping only the literal "Caliber" prefix is
    // less pretty but never actively wrong - "556x45NATO", "9x39",
    // "1143x23ACP" are all still legible groupings even unformatted.
    private static string FormatCaliber(string rawCaliber) =>
        rawCaliber.StartsWith("Caliber", StringComparison.Ordinal)
            ? rawCaliber["Caliber".Length..]
            : rawCaliber;
}
