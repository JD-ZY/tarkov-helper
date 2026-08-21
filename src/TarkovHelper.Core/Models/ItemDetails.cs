namespace TarkovHelper.Core.Models;

public class TraderOffer
{
    public required string TraderName { get; init; }
    public required int PriceRub { get; init; }
}

// Catalog-level item info (price/sell data) - distinct from the lightweight
// Item used inside quest objectives/hideout requirements, since price is a
// property of the item itself, not of any one place it's referenced.
public class ItemDetails
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    // Rolling 24h flea market average, in RUB - null if the item is
    // currently untradeable/flea-restricted rather than genuinely worth 0,
    // so callers must not treat null as "worth nothing."
    public int? FleaPriceRub { get; init; }

    // The single highest-paying trader offer, already converted to RUB
    // (sellToTrader entries can be priced in RUB or USD/EUR - tarkov.dev's
    // priceRUB field is the pre-converted value, avoiding a currency
    // conversion step here) - null if no trader buys this item at all.
    public TraderOffer? BestTraderOffer { get; init; }
}
