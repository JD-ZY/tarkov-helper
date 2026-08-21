using System.Text.Json;
using TarkovHelper.Core.JsonFallback;

namespace TarkovHelper.Core.Tests;

// Fixtures are real, captured responses from json.tarkov.dev's /maps
// endpoint, not synthetic.
public class JsonTarkovDevClientExtractsTests
{
    private static JsonElement LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static Dictionary<string, string> LoadTranslations() =>
        JsonTarkovDevClient.ParseTranslationDictionary(LoadFixture("maps-extracts-en-sample.json"));

    [Fact]
    public void ParseExtracts_GroupsByMapNormalizedName()
    {
        var mapsRoot = LoadFixture("maps-extracts-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseExtracts(mapsRoot, translations);

        Assert.True(result.ContainsKey("customs"));
        Assert.True(result.ContainsKey("factory"));
    }

    [Fact]
    public void ParseExtracts_ResolvesTranslatedExtractName()
    {
        var mapsRoot = LoadFixture("maps-extracts-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseExtracts(mapsRoot, translations);
        var customsExtracts = result["customs"];

        // Real raw name is "EXFIL_ZB013", real translated display name is "ZB-013".
        Assert.Contains(customsExtracts, e => e.Name == "ZB-013");
        Assert.DoesNotContain(customsExtracts, e => e.Name == "EXFIL_ZB013");
    }

    [Fact]
    public void ParseExtracts_PreservesFactionAndPosition()
    {
        var mapsRoot = LoadFixture("maps-extracts-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseExtracts(mapsRoot, translations);
        var extract = result["customs"].Single(e => e.Name == "ZB-013");

        Assert.Equal("pmc", extract.Faction);
        Assert.Equal(200.9755f, extract.X, precision: 2);
        Assert.Equal(-153.086456f, extract.Z, precision: 2);
    }

    [Fact]
    public void ParseExtracts_MissingTranslation_FallsBackToRawName()
    {
        var mapsRoot = LoadFixture("maps-extracts-sample.json");
        var emptyTranslations = new Dictionary<string, string>();

        var result = JsonTarkovDevClient.ParseExtracts(mapsRoot, emptyTranslations);

        Assert.Contains(result["customs"], e => e.Name == "EXFIL_ZB013");
    }

    [Fact]
    public void ParseExtracts_UnknownMap_KeyAbsent()
    {
        var mapsRoot = LoadFixture("maps-extracts-sample.json");
        var translations = LoadTranslations();

        var result = JsonTarkovDevClient.ParseExtracts(mapsRoot, translations);

        Assert.False(result.ContainsKey("woods"));
    }
}
