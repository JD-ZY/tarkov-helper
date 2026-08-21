using System.Text.Json;
using TarkovHelper.Core.JsonFallback;

namespace TarkovHelper.Core.Tests;

// Fixtures are real captured responses from json.tarkov.dev/hideout, not
// synthetic - includes Library, Workbench, and Gear Rack.
public class JsonTarkovDevClientHideoutTests
{
    private static JsonElement LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static Dictionary<string, string> LoadTranslations() =>
        JsonTarkovDevClient.ParseTranslationDictionary(LoadFixture("hideout-en-sample.json"));

    [Fact]
    public void ParseHideoutStations_ResolvesStationNameFromLiteralTranslationKey()
    {
        // Regression test: the "name" field IS the translation key verbatim
        // ("hideout_area_13_name"), unlike tasks/items where the key is
        // built as "<id> <suffix>" - an earlier version of this parser got
        // this wrong and silently fell back to the raw untranslated key for
        // every station.
        var hideoutRoot = LoadFixture("hideout-sample.json");
        var translations = LoadTranslations();

        var stations = JsonTarkovDevClient.ParseHideoutStations(
            hideoutRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>());

        Assert.Contains(stations, s => s.Name == "Library");
        Assert.Contains(stations, s => s.Name == "Workbench");
        Assert.Contains(stations, s => s.Name == "Gear Rack");
        Assert.DoesNotContain(stations, s => s.Name.StartsWith("hideout_area_"));
    }

    [Fact]
    public void ParseHideoutStations_ParsesLevelsAndItemRequirements()
    {
        var hideoutRoot = LoadFixture("hideout-sample.json");
        var translations = LoadTranslations();
        var itemNames = new Dictionary<string, string> { ["5449016a4bdc2d6f028b456f"] = "Roubles" };

        var stations = JsonTarkovDevClient.ParseHideoutStations(
            hideoutRoot, translations, new Dictionary<string, string>(), itemNames);

        var library = stations.Single(s => s.Name == "Library");
        Assert.Single(library.Levels);
        var level1 = library.Levels[0];
        Assert.Equal(1, level1.Level);
        Assert.Contains(level1.ItemRequirements, r => r.Item.Name == "Roubles" && r.Count == 400000);
    }

    [Fact]
    public void ParseHideoutStations_ResolvesStationLevelRequirementByName()
    {
        var hideoutRoot = LoadFixture("hideout-sample.json");
        var translations = LoadTranslations();

        var stations = JsonTarkovDevClient.ParseHideoutStations(
            hideoutRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>());

        // Library level 1 requires Rest Space level 3 (verified in the
        // real fixture data).
        var library = stations.Single(s => s.Name == "Library");
        var requirement = library.Levels[0].StationRequirements.Single();
        Assert.Equal("Rest Space", requirement.StationName);
        Assert.Equal(3, requirement.Level);
    }

    [Fact]
    public void ParseHideoutStations_ResolvesTraderRequirementName()
    {
        var hideoutRoot = LoadFixture("hideout-sample.json");
        var translations = LoadTranslations();
        var traderNames = new Dictionary<string, string> { ["5ac3b934156ae10c4430e83c"] = "Ragman" };

        var stations = JsonTarkovDevClient.ParseHideoutStations(
            hideoutRoot, translations, traderNames, new Dictionary<string, string>());

        var gearRack = stations.Single(s => s.Name == "Gear Rack");
        var traderReq = gearRack.Levels[0].TraderRequirements.Single();
        Assert.Equal("Ragman", traderReq.TraderName);
        Assert.Equal(2, traderReq.Level);
    }

    [Fact]
    public void ParseHideoutStations_MissingTranslation_FallsBackToRawKeyOrId()
    {
        var hideoutRoot = LoadFixture("hideout-sample.json");
        var emptyTranslations = new Dictionary<string, string>();

        var stations = JsonTarkovDevClient.ParseHideoutStations(
            hideoutRoot, emptyTranslations, new Dictionary<string, string>(), new Dictionary<string, string>());

        Assert.Contains(stations, s => s.Name == "hideout_area_13_name");
    }
}
