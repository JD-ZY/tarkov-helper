using System.Text.Json;
using TarkovHelper.Core.JsonFallback;

namespace TarkovHelper.Core.Tests;

// Fixture is a real, captured task with zone data ("First in Line",
// id 657315ddab5a49b71f098853) from json.tarkov.dev, not synthetic.
public class JsonTarkovDevClientZonesTests
{
    private static JsonElement LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static readonly Dictionary<string, string> MapNames = new()
    {
        ["653e6760052c01c1c805532f"] = "ground-zero",
        ["65b8d6f5cdde2479cb2a3125"] = "ground-zero-21",
    };

    [Fact]
    public void ParseTasks_PopulatesObjectiveZonesWithResolvedMapName()
    {
        var tasksRoot = LoadFixture("tasks-with-zones-sample.json");
        var translations = JsonTarkovDevClient.ParseTranslationDictionary(tasksRoot);

        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>(), MapNames);

        var task = tasks.Single(t => t.Id == "657315ddab5a49b71f098853");
        var objective = task.Objectives.Single(o => o.Type == "visit");

        Assert.Equal(2, objective.Zones.Count);
        Assert.Contains(objective.Zones, z => z.MapNormalizedName == "ground-zero");
        Assert.Contains(objective.Zones, z => z.MapNormalizedName == "ground-zero-21");
    }

    [Fact]
    public void ParseTasks_ZonePositionMatchesRealCoordinates()
    {
        var tasksRoot = LoadFixture("tasks-with-zones-sample.json");
        var translations = JsonTarkovDevClient.ParseTranslationDictionary(tasksRoot);

        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>(), MapNames);

        var zone = tasks.Single(t => t.Id == "657315ddab5a49b71f098853")
            .Objectives.Single(o => o.Type == "visit").Zones.First();

        Assert.Equal(156.2f, zone.X, precision: 1);
        Assert.Equal(-83.59f, zone.Z, precision: 1);
    }

    [Fact]
    public void ParseTasks_UnresolvableMapId_ZoneExcludedRatherThanNull()
    {
        var tasksRoot = LoadFixture("tasks-with-zones-sample.json");
        var translations = JsonTarkovDevClient.ParseTranslationDictionary(tasksRoot);

        // No map lookup provided - zones reference unresolvable map IDs.
        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>());

        var objective = tasks.Single(t => t.Id == "657315ddab5a49b71f098853").Objectives.Single(o => o.Type == "visit");

        Assert.Empty(objective.Zones);
    }

    // Real bug found reviewing a user's actual quest cache: the "Work
    // Smarter" task's "Locate the secret exfil on Customs" objective has
    // the exact same zone entry (same map, same position) repeated twice
    // verbatim in the raw data - without dedup, this double-weights that
    // position in cluster-centroid math relative to genuinely distinct
    // positions on the same objective.
    [Fact]
    public void ParseTasks_DuplicateZoneEntries_DedupedToOne()
    {
        var mapNames = new Dictionary<string, string> { ["56f40101d2720b2a4d8b45d6"] = "customs" };
        var tasksRoot = JsonSerializer.Deserialize<JsonElement>("""
            {
              "data": {
                "tasks": {
                  "task1": {
                    "id": "task1",
                    "trader": "t1",
                    "objectives": [
                      {
                        "id": "obj1",
                        "type": "visit",
                        "description": "obj1",
                        "zones": [
                          { "map": "56f40101d2720b2a4d8b45d6", "position": { "x": -41.51, "z": 122.67 } },
                          { "map": "56f40101d2720b2a4d8b45d6", "position": { "x": -41.51, "z": 122.67 } },
                          { "map": "56f40101d2720b2a4d8b45d6", "position": { "x": 463.18, "z": -112.36 } }
                        ]
                      }
                    ]
                  }
                }
              },
              "translations": []
            }
            """);

        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            mapNames);

        var zones = tasks.Single().Objectives.Single().Zones;

        Assert.Equal(2, zones.Count);
    }

    // Real bug reported by a user: the quest grid's Map column showed "Any
    // map" for every single quest. Root cause: JsonTaskDto never declared
    // a "map" property at all, so System.Text.Json silently dropped the
    // raw JSON's real per-task map ID (verified live: "First in Line"'s
    // real data has "map": "653e6760052c01c1c805532f", i.e. Ground Zero)
    // and QuestTask.Map was never populated by ParseTasks.
    [Fact]
    public void ParseTasks_PopulatesTaskMapFromRealData()
    {
        var tasksRoot = LoadFixture("tasks-with-zones-sample.json");
        var translations = JsonTarkovDevClient.ParseTranslationDictionary(tasksRoot);

        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>(), MapNames);

        var task = tasks.Single(t => t.Id == "657315ddab5a49b71f098853");

        Assert.NotNull(task.Map);
        Assert.Equal("653e6760052c01c1c805532f", task.Map!.Id);
        Assert.Equal("ground-zero", task.Map.NormalizedName);
        Assert.Equal("Ground Zero", task.Map.Name);
    }

    [Fact]
    public void ParseTasks_UnresolvableTaskMapId_MapIsNullRatherThanThrowing()
    {
        var tasksRoot = LoadFixture("tasks-with-zones-sample.json");
        var translations = JsonTarkovDevClient.ParseTranslationDictionary(tasksRoot);

        // No map lookup provided - the task's top-level map ID can't be resolved.
        var tasks = JsonTarkovDevClient.ParseTasks(
            tasksRoot, translations, new Dictionary<string, string>(), new Dictionary<string, string>());

        var task = tasks.Single(t => t.Id == "657315ddab5a49b71f098853");

        Assert.Null(task.Map);
    }
}
