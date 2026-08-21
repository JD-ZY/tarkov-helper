using System.Text.Json;
using TarkovHelper.Core.JsonFallback;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.Tests;

// Fixtures are real, captured positions/container types from json.tarkov.dev's
// /maps endpoint (data.maps[].lootContainers + data.lootContainers catalog),
// not synthetic.
public class JsonTarkovDevClientLootContainersTests
{
    private static JsonElement LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static Dictionary<string, string> LoadTranslations() =>
        JsonTarkovDevClient.ParseTranslationDictionary(LoadFixture("maps-loot-containers-en-sample.json"));

    [Fact]
    public void ParseLootContainers_GroupsByMapNormalizedName()
    {
        var mapsRoot = LoadFixture("maps-loot-containers-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseLootContainers(mapsRoot, translations);

        Assert.True(result.ContainsKey("customs"));
        Assert.True(result.ContainsKey("factory"));
        Assert.Equal(4, result["customs"].Count);
        Assert.Empty(result["factory"]);
    }

    [Fact]
    public void ParseLootContainers_ResolvesContainerTypeAndTranslatedName()
    {
        var mapsRoot = LoadFixture("maps-loot-containers-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseLootContainers(mapsRoot, translations);

        Assert.Contains(result["customs"], c => c.ContainerNormalizedName == "safe" && c.ContainerName == "Safe");
        Assert.Contains(result["customs"], c => c.ContainerNormalizedName == "duffle-bag" && c.ContainerName == "Duffle bag");
    }

    [Fact]
    public void ParseLootContainers_PreservesRealPosition()
    {
        var mapsRoot = LoadFixture("maps-loot-containers-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseLootContainers(mapsRoot, translations);
        var safe = result["customs"].Single(c => c.ContainerNormalizedName == "safe");

        Assert.Equal(226.868713f, safe.X, precision: 2);
        Assert.Equal(136.745f, safe.Z, precision: 2);
    }

    [Fact]
    public void ParseLootContainers_MissingTranslation_FallsBackToNormalizedName()
    {
        var mapsRoot = LoadFixture("maps-loot-containers-sample.json");
        var emptyTranslations = new Dictionary<string, string>();

        var result = JsonTarkovDevClient.ParseLootContainers(mapsRoot, emptyTranslations);

        Assert.Contains(result["customs"], c => c.ContainerNormalizedName == "safe" && c.ContainerName == "safe");
    }

    [Theory]
    [InlineData("safe", LootTier.High)]
    [InlineData("bank-safe", LootTier.High)]
    [InlineData("weapon-box", LootTier.High)]
    [InlineData("jacket", LootTier.Medium)]
    [InlineData("wooden-crate", LootTier.Low)]
    [InlineData("some-new-container-type-not-yet-seen", LootTier.Medium)]
    public void GetTier_ReturnsExpectedTier(string normalizedName, LootTier expected)
    {
        Assert.Equal(expected, LootContainerTiers.GetTier(normalizedName));
    }
}
