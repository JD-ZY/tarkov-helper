using System.Text.Json;
using TarkovHelper.Core.JsonFallback;

namespace TarkovHelper.Core.Tests;

// Fixture shapes are real, live-verified data from
// https://json.tarkov.dev/regular/items (fetched directly during
// development - a genuine 5.56x45mm M855 entry, not synthesized from
// documentation), since json.tarkov.dev is the confirmed-working fallback
// while api.tarkov.dev/graphql has been down for an extended, publicly
// tracked outage (the-hideout/tarkov-api#474).
public class JsonTarkovDevClientAmmoTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static readonly Dictionary<string, string> Translations = new()
    {
        ["ammo1 Name"] = "5.56x45mm M855",
        ["ammo1 ShortName"] = "M855",
    };

    private static readonly Dictionary<string, string> TraderNames = new()
    {
        ["5935c25fb3acc3127c3d8cd9"] = "Peacekeeper",
    };

    private static readonly Dictionary<string, string> TaskNames = new()
    {
        ["5a68665c86f774255929b4c7"] = "Health Care Privacy - Part 3",
    };

    [Fact]
    public void ParseAmmo_ParsesBallisticFieldsFromNestedProperties()
    {
        var json = """
            {
              "data": {
                "items": {
                  "ammo1": {
                    "id": "ammo1",
                    "name": "ammo1 Name",
                    "shortName": "ammo1 ShortName",
                    "properties": {
                      "propertiesType": "ItemPropertiesAmmo",
                      "caliber": "Caliber556x45NATO",
                      "damage": 54,
                      "armorDamage": 37,
                      "penetrationPower": 31,
                      "fragmentationChance": 0.5,
                      "ricochetChance": 0.4,
                      "initialSpeed": 922,
                      "accuracyModifier": 0,
                      "recoilModifier": 0,
                      "tracer": false
                    }
                  }
                }
              }
            }
            """;

        var result = JsonTarkovDevClient.ParseAmmo(Parse(json), Translations, TraderNames, TaskNames);
        var ammo = Assert.Single(result);

        Assert.Equal("ammo1", ammo.Id);
        Assert.Equal("5.56x45mm M855", ammo.Name);
        Assert.Equal("M855", ammo.ShortName);
        Assert.Equal("Caliber556x45NATO", ammo.Caliber);
        Assert.Equal(54, ammo.Damage);
        Assert.Equal(37, ammo.ArmorDamage);
        Assert.Equal(31, ammo.PenetrationPower);
        Assert.Equal(922, ammo.InitialSpeed);
        Assert.False(ammo.Tracer);
    }

    [Fact]
    public void ParseAmmo_NonAmmoItem_IsExcluded()
    {
        // Real bug this guards against: grenades are also tagged
        // types:["ammo"] in tarkov.dev's data but have propertiesType
        // "ItemPropertiesGrenade" instead of "ItemPropertiesAmmo" -
        // filtering on the properties tag (not item "types") is required
        // to avoid pulling grenades into the ammo chart.
        var json = """
            {
              "data": {
                "items": {
                  "grenade1": {
                    "id": "grenade1",
                    "name": "grenade1 Name",
                    "properties": {
                      "propertiesType": "ItemPropertiesGrenade"
                    }
                  }
                }
              }
            }
            """;

        var result = JsonTarkovDevClient.ParseAmmo(Parse(json), Translations, TraderNames, TaskNames);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseAmmo_TraderOfferWithLoyaltyLevel_ResolvesTraderNameAndLevel()
    {
        var json = """
            {
              "data": {
                "items": {
                  "ammo1": {
                    "id": "ammo1", "name": "ammo1 Name", "shortName": "ammo1 ShortName",
                    "properties": { "propertiesType": "ItemPropertiesAmmo", "caliber": "X", "damage": 1, "armorDamage": 1, "penetrationPower": 1, "fragmentationChance": 0, "ricochetChance": 0, "initialSpeed": 1, "tracer": false },
                    "buyFromTrader": [
                      { "trader": "5935c25fb3acc3127c3d8cd9", "priceRUB": 370, "minTraderLevel": 3 }
                    ]
                  }
                }
              }
            }
            """;

        var result = JsonTarkovDevClient.ParseAmmo(Parse(json), Translations, TraderNames, TaskNames);
        var offer = Assert.Single(result[0].TraderOffers);

        Assert.Equal("Peacekeeper", offer.TraderName);
        Assert.Equal(3, offer.LoyaltyLevel);
        Assert.Equal(370, offer.PriceRub);
        Assert.Null(offer.RequiredQuestName);
    }

    [Fact]
    public void ParseAmmo_TraderOfferWithTaskUnlock_ResolvesQuestName()
    {
        var json = """
            {
              "data": {
                "items": {
                  "ammo1": {
                    "id": "ammo1", "name": "ammo1 Name", "shortName": "ammo1 ShortName",
                    "properties": { "propertiesType": "ItemPropertiesAmmo", "caliber": "X", "damage": 1, "armorDamage": 1, "penetrationPower": 1, "fragmentationChance": 0, "ricochetChance": 0, "initialSpeed": 1, "tracer": false },
                    "buyFromTrader": [
                      { "trader": "5935c25fb3acc3127c3d8cd9", "priceRUB": 36581, "minTraderLevel": 4, "taskUnlock": "5a68665c86f774255929b4c7" }
                    ]
                  }
                }
              }
            }
            """;

        var result = JsonTarkovDevClient.ParseAmmo(Parse(json), Translations, TraderNames, TaskNames);
        var offer = Assert.Single(result[0].TraderOffers);

        Assert.Equal("Health Care Privacy - Part 3", offer.RequiredQuestName);
    }

    [Fact]
    public void ParseAmmo_MissingMinTraderLevel_IsExcludedFromTraderOffers()
    {
        // Real bug this guards against: json.tarkov.dev's buyFromTrader
        // entries always carry minTraderLevel for genuine trader offers -
        // an entry missing it isn't a valid trader offer this app can act
        // on (no loyalty gate to check), so it must not silently default
        // to e.g. level 0 and be shown as freely available.
        var json = """
            {
              "data": {
                "items": {
                  "ammo1": {
                    "id": "ammo1", "name": "ammo1 Name", "shortName": "ammo1 ShortName",
                    "properties": { "propertiesType": "ItemPropertiesAmmo", "caliber": "X", "damage": 1, "armorDamage": 1, "penetrationPower": 1, "fragmentationChance": 0, "ricochetChance": 0, "initialSpeed": 1, "tracer": false },
                    "buyFromTrader": [
                      { "trader": "5935c25fb3acc3127c3d8cd9", "priceRUB": 370 }
                    ]
                  }
                }
              }
            }
            """;

        var result = JsonTarkovDevClient.ParseAmmo(Parse(json), Translations, TraderNames, TaskNames);

        Assert.Empty(result[0].TraderOffers);
    }

    [Fact]
    public void ParseAmmo_UnresolvedTranslation_FallsBackToItemId()
    {
        var json = """
            {
              "data": {
                "items": {
                  "ammo1": {
                    "id": "ammo1", "name": "unknown-translation-key",
                    "properties": { "propertiesType": "ItemPropertiesAmmo", "caliber": "X", "damage": 1, "armorDamage": 1, "penetrationPower": 1, "fragmentationChance": 0, "ricochetChance": 0, "initialSpeed": 1, "tracer": false }
                  }
                }
              }
            }
            """;

        var result = JsonTarkovDevClient.ParseAmmo(Parse(json), Translations, TraderNames, TaskNames);

        Assert.Equal("ammo1", result[0].Name);
    }
}
