using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core;

public class TarkovDevClient
{
    private const string Endpoint = "https://api.tarkov.dev/graphql";
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TarkovDevClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
    }

    // Field names verified against the-hideout/tarkov-api schema-static.mjs:
    // Task.map is singular; TaskObjective.maps is plural. Inline fragments
    // are required to read type-specific objective fields (interface type).
    // gameMode is a $variable (not inlined into the query string) so the
    // same static query serves both regular and PvE requests - schema-static.mjs
    // confirms tasks() accepts gameMode: GameMode with values "regular"/"pve",
    // and PvE genuinely has a different task set (some tasks exist only in
    // one mode, e.g. "[PVP ZONE]"/"[PVE ZONE]"-suffixed variants of the same
    // quest line - verified directly against live tarkov.dev data).
    private const string TasksQuery = """
        query TasksQuery($gameMode: GameMode) {
          tasks(lang: en, gameMode: $gameMode) {
            id
            name
            normalizedName
            minPlayerLevel
            wikiLink
            kappaRequired
            trader { id name }
            map { id name normalizedName }
            taskRequirements {
              task { id }
              status
            }
            objectives {
              id
              type
              description
              optional
              maps { id name normalizedName }
              ... on TaskObjectiveItem {
                items { id name shortName }
                count
                foundInRaid
              }
              ... on TaskObjectiveBasic {
                zones { map { normalizedName } position { x z } }
              }
              ... on TaskObjectiveMark {
                zones { map { normalizedName } position { x z } }
              }
              ... on TaskObjectiveShoot {
                zones { map { normalizedName } position { x z } }
              }
              ... on TaskObjectiveUseItem {
                zones { map { normalizedName } position { x z } }
              }
              ... on TaskObjectiveQuestItem {
                zones { map { normalizedName } position { x z } }
                possibleLocations { map { normalizedName } positions { x z } }
              }
            }
          }
        }
        """;

    public async Task<List<QuestTask>> GetTasksAsync(GameMode gameMode = GameMode.Regular, CancellationToken ct = default)
    {
        // tarkov-api's GameMode enum values are "regular"/"pve" (lowercase,
        // verified against schema-static.mjs) - not the C# enum's own
        // Pascal-cased member names, so this can't just be gameMode.ToString().
        var gameModeValue = gameMode == GameMode.Pve ? "pve" : "regular";
        var payload = new { query = TasksQuery, variables = new { gameMode = gameModeValue } };
        using var response = await _http.PostAsJsonAsync(Endpoint, payload, ct);

        // The API returns non-2xx (e.g. 422/503) with a JSON error body during
        // outages, so parse errors[] before bailing out on status code alone -
        // otherwise the underlying message ("GraphQL server unavailable") is
        // lost in favor of a generic HttpRequestException.
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array
            && errorsElement.GetArrayLength() > 0)
        {
            throw new InvalidOperationException($"tarkov.dev API error: {ExtractErrorMessages(errorsElement)}");
        }

        response.EnsureSuccessStatusCode();

        if (!root.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("tasks", out var tasksElement))
        {
            return new List<QuestTask>();
        }

        var tasks = tasksElement.Deserialize<List<QuestTask>?>(JsonOptions) ?? new List<QuestTask>();

        // ObjectiveZone's flat (MapNormalizedName, X, Z) shape doesn't match
        // GraphQL's nested zones[].map.normalizedName/position.{x,z} (or
        // possibleLocations[].map.normalizedName/positions[].{x,z}) closely
        // enough for direct property-name deserialization to populate it -
        // walk the raw response alongside the deserialized tasks (same
        // ordinal position in both, since neither is sorted/filtered) and
        // fill Zones in manually, same transform BuildZones() does for the
        // JSON fallback client.
        PopulateObjectiveZonesFromRaw(tasks, tasksElement);

        return tasks;
    }

    private static void PopulateObjectiveZonesFromRaw(List<QuestTask> tasks, JsonElement tasksElement)
    {
        var rawTasks = tasksElement.EnumerateArray().ToList();
        if (rawTasks.Count != tasks.Count)
        {
            return;
        }

        for (var i = 0; i < tasks.Count; i++)
        {
            if (!rawTasks[i].TryGetProperty("objectives", out var rawObjectivesElement) ||
                rawObjectivesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var rawObjectives = rawObjectivesElement.EnumerateArray().ToList();
            var objectives = tasks[i].Objectives;
            if (rawObjectives.Count != objectives.Count)
            {
                continue;
            }

            for (var j = 0; j < objectives.Count; j++)
            {
                objectives[j].Zones = ParseZones(rawObjectives[j]);
            }
        }
    }

    private static List<ObjectiveZone> ParseZones(JsonElement rawObjective)
    {
        var zones = new List<ObjectiveZone>();

        if (rawObjective.TryGetProperty("zones", out var zonesElement) && zonesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var zone in zonesElement.EnumerateArray())
            {
                var mapName = zone.TryGetProperty("map", out var mapEl) && mapEl.ValueKind == JsonValueKind.Object
                    && mapEl.TryGetProperty("normalizedName", out var nameEl) ? nameEl.GetString() : null;

                if (mapName is null || !zone.TryGetProperty("position", out var posEl) || posEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                zones.Add(new ObjectiveZone
                {
                    MapNormalizedName = mapName,
                    X = posEl.TryGetProperty("x", out var xEl) ? xEl.GetSingle() : 0f,
                    Z = posEl.TryGetProperty("z", out var zEl) ? zEl.GetSingle() : 0f,
                });
            }
        }

        if (rawObjective.TryGetProperty("possibleLocations", out var locationsElement) && locationsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in locationsElement.EnumerateArray())
            {
                var mapName = location.TryGetProperty("map", out var mapEl) && mapEl.ValueKind == JsonValueKind.Object
                    && mapEl.TryGetProperty("normalizedName", out var nameEl) ? nameEl.GetString() : null;

                if (mapName is null || !location.TryGetProperty("positions", out var positionsEl) || positionsEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var pos in positionsEl.EnumerateArray())
                {
                    zones.Add(new ObjectiveZone
                    {
                        MapNormalizedName = mapName,
                        X = pos.TryGetProperty("x", out var xEl) ? xEl.GetSingle() : 0f,
                        Z = pos.TryGetProperty("z", out var zEl) ? zEl.GetSingle() : 0f,
                    });
                }
            }
        }

        return zones.DistinctBy(z => (z.MapNormalizedName, z.X, z.Z)).ToList();
    }

    // The API normally returns errors[] as objects with a "message" field
    // (GraphQL-over-HTTP spec, via graphql-yoga), but falls back to a bare
    // string[] when the whole worker is down (observed directly: HTTP 503,
    // body {"errors":["GraphQL server unavailable. Try again later."]}).
    // Handle both shapes defensively rather than assuming one.
    private static string ExtractErrorMessages(JsonElement errorsElement)
    {
        var messages = new List<string>();
        foreach (var error in errorsElement.EnumerateArray())
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                messages.Add(error.GetString() ?? "unknown error");
            }
            else if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var msg))
            {
                messages.Add(msg.GetString() ?? "unknown error");
            }
        }
        return string.Join("; ", messages);
    }
}
