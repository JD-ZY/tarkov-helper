using System.Text.Json;
using TarkovHelper.Core.JsonFallback;

namespace TarkovHelper.Core.Tests;

// Fixture is real, captured data from json.tarkov.dev's /items endpoint
// (Colt M4A1's real avg24hPrice/sellToTrader entries), not synthetic.
public class JsonTarkovDevClientItemDetailsTests
{
    private static JsonElement LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static Dictionary<string, string> LoadTranslations() =>
        JsonTarkovDevClient.ParseTranslationDictionary(LoadFixture("items-with-prices-en-sample.json"));

    private static readonly Dictionary<string, string> TraderNames = new()
    {
        ["54cb50c76803fa8b248b4571"] = "Therapist",
        ["579dc571d53a0658a154fbec"] = "Skier",
        ["5935c25fb3acc3127c3d8cd9"] = "Ragman",
    };

    [Fact]
    public void ParseItemDetails_ResolvesTranslatedNameAndFleaPrice()
    {
        var itemsRoot = LoadFixture("items-with-prices-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseItemDetails(itemsRoot, translations, TraderNames);
        var item = result["5447a9cd4bdc2dbd208b4567"];

        Assert.Equal("Colt M4A1 5.56x45 assault rifle", item.Name);
        Assert.Equal(43360, item.FleaPriceRub);
    }

    [Fact]
    public void ParseItemDetails_PicksHighestPriceRubOfferAsBest()
    {
        var itemsRoot = LoadFixture("items-with-prices-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseItemDetails(itemsRoot, translations, TraderNames);
        var item = result["5447a9cd4bdc2dbd208b4567"];

        // Therapist 7358 RUB > Ragman's USD offer converted to 6622 RUB >
        // Skier 4415 RUB - Therapist should win despite not being listed
        // first, and despite Ragman's raw non-RUB price (55) being
        // numerically smaller than priceRUB implies if compared wrong.
        Assert.NotNull(item.BestTraderOffer);
        Assert.Equal("Therapist", item.BestTraderOffer!.TraderName);
        Assert.Equal(7358, item.BestTraderOffer.PriceRub);
    }

    [Fact]
    public void ParseItemDetails_NoTraderOffers_BestOfferIsNull()
    {
        var itemsRoot = LoadFixture("items-with-prices-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseItemDetails(itemsRoot, translations, TraderNames);
        var item = result["5734758f24597738025ee253"];

        Assert.Null(item.BestTraderOffer);
    }

    [Fact]
    public void ParseItemDetails_NullFleaPrice_PreservedAsNullNotZero()
    {
        // A null avg24hPrice means untradeable/flea-restricted, not "worth
        // 0" - callers must be able to tell the difference.
        var itemsRoot = LoadFixture("items-with-prices-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseItemDetails(itemsRoot, translations, TraderNames);
        var item = result["5734758f24597738025ee253"];

        Assert.Null(item.FleaPriceRub);
    }
}
