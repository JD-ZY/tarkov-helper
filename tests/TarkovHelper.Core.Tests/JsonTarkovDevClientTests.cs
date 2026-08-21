using System.Text.Json;
using TarkovHelper.Core.JsonFallback;

namespace TarkovHelper.Core.Tests;

// All fixtures are real, captured responses from json.tarkov.dev (the
// fallback REST/R2 mirror used while api.tarkov.dev/graphql has been down
// for an extended outage - the-hideout/tarkov-api#474), not synthetic.
public class JsonTarkovDevClientTests
{
    private static JsonElement LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static Dictionary<string, string> LoadTranslations(string filename) =>
        JsonTarkovDevClient.ParseTranslationDictionary(LoadFixture(filename));

    [Fact]
    public void ParseTranslationDictionary_ResolvesRealTaskNameKeys()
    {
        var translations = LoadTranslations("tasks-en-sample.json");

        Assert.Equal("Debut", translations["5936d90786f7742b1420ba5b name"]);
        Assert.Equal("First in Line", translations["657315ddab5a49b71f098853 name"]);
    }

    [Fact]
    public void ParseTranslationDictionary_ResolvesTraderNicknameKey_DifferentSuffixThanTasks()
    {
        // Traders use "<id> Nickname", not "<id> name" like tasks - the
        // suffix is not a uniform convention across endpoints.
        var translations = LoadTranslations("traders-en-sample.json");

        Assert.Equal("Prapor", translations["54cb50c76803fa8b248b4571 Nickname"]);
        Assert.Equal("Therapist", translations["54cb57776803fa99248b456e Nickname"]);
    }

    [Fact]
    public void ParseTranslationDictionary_ResolvesItemNameKey_CapitalNSuffix()
    {
        // Items use "<id> Name" (capital N), yet another distinct casing
        // from tasks' lowercase "<id> name".
        var translations = LoadTranslations("items-en-sample.json");

        Assert.Equal("MP-133 12ga pump-action shotgun", translations["54491c4f4bdc2db1078b4568 Name"]);
        Assert.Equal("MP-133", translations["54491c4f4bdc2db1078b4568 ShortName"]);
    }

    [Fact]
    public void ParseTasks_ResolvesTaskNamesFromPlaceholderIds()
    {
        var tasksRoot = LoadFixture("tasks-sample.json");
        var translations = LoadTranslations("tasks-en-sample.json");
        var traderNames = new Dictionary<string, string> { ["54cb50c76803fa8b248b4571"] = "Prapor" };
        var itemNames = new Dictionary<string, string>();

        var tasks = JsonTarkovDevClient.ParseTasks(tasksRoot, translations, traderNames, itemNames);

        var debut = tasks.Single(t => t.Id == "5936d90786f7742b1420ba5b");
        Assert.Equal("Debut", debut.Name);
    }

    [Fact]
    public void ParseTasks_ResolvesTraderName()
    {
        var tasksRoot = LoadFixture("tasks-sample.json");
        var translations = LoadTranslations("tasks-en-sample.json");
        var traderNames = new Dictionary<string, string> { ["54cb50c76803fa8b248b4571"] = "Prapor" };
        var itemNames = new Dictionary<string, string>();

        var tasks = JsonTarkovDevClient.ParseTasks(tasksRoot, translations, traderNames, itemNames);

        var debut = tasks.Single(t => t.Id == "5936d90786f7742b1420ba5b");
        Assert.Equal("Prapor", debut.Trader.Name);
    }

    [Fact]
    public void ParseTasks_ResolvesObjectiveDescriptions()
    {
        var tasksRoot = LoadFixture("tasks-sample.json");
        var translations = LoadTranslations("tasks-en-sample.json");
        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>());

        var debut = tasks.Single(t => t.Id == "5936d90786f7742b1420ba5b");
        var descriptions = debut.Objectives.Select(o => o.Description).ToList();

        Assert.Contains("Eliminate Scavs on any location", descriptions);
        Assert.Contains("Hand over the item: MP-133 12ga shotgun", descriptions);
    }

    [Fact]
    public void ParseTasks_ResolvesItemNamesForGiveItemObjective()
    {
        var tasksRoot = LoadFixture("tasks-sample.json");
        var translations = LoadTranslations("tasks-en-sample.json");
        var itemNames = new Dictionary<string, string> { ["54491c4f4bdc2db1078b4568"] = "MP-133" };

        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, translations, new Dictionary<string, string>(), itemNames);

        var debut = tasks.Single(t => t.Id == "5936d90786f7742b1420ba5b");
        var giveItemObjective = debut.Objectives.Single(o => o.Type == "giveItem");

        Assert.Single(giveItemObjective.Items);
        Assert.Equal("MP-133", giveItemObjective.Items[0].Name);
    }

    [Fact]
    public void ParseTasks_PreservesTaskRequirements()
    {
        var tasksRoot = LoadFixture("tasks-sample.json");
        var translations = LoadTranslations("tasks-en-sample.json");
        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>());

        var debut = tasks.Single(t => t.Id == "5936d90786f7742b1420ba5b");

        var requirement = Assert.Single(debut.TaskRequirements);
        Assert.Equal("657315df034d76585f032e01", requirement.Task.Id);
        Assert.Contains("complete", requirement.Status);
    }

    [Fact]
    public void ParseTasks_MissingTranslation_FallsBackToRawKeyRatherThanThrowing()
    {
        var tasksRoot = LoadFixture("tasks-sample.json");
        var emptyTranslations = new Dictionary<string, string>();

        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, emptyTranslations, new Dictionary<string, string>(), new Dictionary<string, string>());

        var debut = tasks.Single(t => t.Id == "5936d90786f7742b1420ba5b");
        Assert.Equal("5936d90786f7742b1420ba5b name", debut.Name);
    }

    [Fact]
    public void ParseTasks_AllThreeSampleTasksParsed()
    {
        var tasksRoot = LoadFixture("tasks-sample.json");
        var translations = LoadTranslations("tasks-en-sample.json");

        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>());

        Assert.Equal(3, tasks.Count);
    }
}
