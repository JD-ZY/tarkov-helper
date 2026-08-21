using System.Text.Json;
using TarkovHelper.Core.JsonFallback;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.Tests;

// Real bug, reproduced by a user: Ground Zero has three separate map
// entries in json.tarkov.dev's /maps endpoint by player-level bracket
// (Sandbox/Sandbox_high/Sandbox_start -> ground-zero/ground-zero-21/
// ground-zero-tutorial), but only one calibration entry exists in
// tarkov-dev's maps.json ("ground-zero"). Without normalizing these
// aliases, objective zones tagged "ground-zero-21" never matched the
// "ground-zero" map window, silently dropping markers for players above
// level 20.
public class JsonTarkovDevClientMapAliasTests
{
    private static JsonElement BuildMapsFixture() => JsonSerializer.Deserialize<JsonElement>("""
        {
          "data": {
            "maps": {
              "653e6760052c01c1c805532f": { "normalizedName": "ground-zero" },
              "65b8d6f5cdde2479cb2a3125": { "normalizedName": "ground-zero-21" },
              "68236e8153654e8c1200798a": { "normalizedName": "ground-zero-tutorial" },
              "56f40101d2720b2a4d8b45d6": { "normalizedName": "customs" },
              "55f2d3fd4bdc2d5f408b4567": { "normalizedName": "factory" },
              "59fc81d786f774390775787e": { "normalizedName": "night-factory" }
            }
          }
        }
        """);

    [Fact]
    public void GroundZeroHighLevelVariant_NormalizesToBaseGroundZero()
    {
        var result = JsonTarkovDevClient.ParseMapNormalizedNames(BuildMapsFixture());

        Assert.Equal("ground-zero", result["65b8d6f5cdde2479cb2a3125"]);
    }

    [Fact]
    public void GroundZeroTutorialVariant_NormalizesToBaseGroundZero()
    {
        var result = JsonTarkovDevClient.ParseMapNormalizedNames(BuildMapsFixture());

        Assert.Equal("ground-zero", result["68236e8153654e8c1200798a"]);
    }

    [Fact]
    public void GroundZeroBaseVariant_UnaffectedByAliasing()
    {
        var result = JsonTarkovDevClient.ParseMapNormalizedNames(BuildMapsFixture());

        Assert.Equal("ground-zero", result["653e6760052c01c1c805532f"]);
    }

    [Fact]
    public void UnrelatedMap_UnaffectedByAliasing()
    {
        var result = JsonTarkovDevClient.ParseMapNormalizedNames(BuildMapsFixture());

        Assert.Equal("customs", result["56f40101d2720b2a4d8b45d6"]);
    }

    [Fact]
    public void ObjectiveZonesOnHighLevelGroundZero_MatchBaseMapAfterAliasing()
    {
        // End-to-end: a task with a zone on "ground-zero-21" should produce
        // a marker that matches when filtering for "ground-zero", proving
        // the alias fixes the actual reported symptom, not just the lookup
        // table in isolation.
        var mapNames = JsonTarkovDevClient.ParseMapNormalizedNames(BuildMapsFixture());

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
                          { "map": "65b8d6f5cdde2479cb2a3125", "position": { "x": 10, "z": 20 } }
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

        var task = tasks.Single();
        task.IsActive = true;

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "ground-zero");

        Assert.Single(markers);
    }

    // Real bug: Factory has a separate map entry for the night variant
    // ("night-factory"), but only one calibration entry exists ("factory"),
    // and MapNameResolver collapses both factory4_day/factory4_night raid
    // location IDs to "factory". Without aliasing, 14 real quests' Factory
    // objectives (verified against the user's real quest cache) tagged
    // "night-factory" silently never rendered on either Factory raid.
    [Fact]
    public void NightFactoryVariant_NormalizesToBaseFactory()
    {
        var result = JsonTarkovDevClient.ParseMapNormalizedNames(BuildMapsFixture());

        Assert.Equal("factory", result["59fc81d786f774390775787e"]);
    }

    [Fact]
    public void ObjectiveZonesOnNightFactory_MatchBaseMapAfterAliasing()
    {
        var mapNames = JsonTarkovDevClient.ParseMapNormalizedNames(BuildMapsFixture());

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
                        "type": "shoot",
                        "description": "obj1",
                        "zones": [
                          { "map": "59fc81d786f774390775787e", "position": { "x": 10, "z": 20 } }
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

        var task = tasks.Single();
        task.IsActive = true;

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "factory");

        Assert.Single(markers);
    }
}
