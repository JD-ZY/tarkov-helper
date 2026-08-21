using System.Text.Json;
using TarkovHelper.Core.JsonFallback;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core;

// Caches hideout station data locally and persists the player's current
// level per station, which is local-only state - EFT has no log event for
// hideout construction (verified: TarkovMonitor's own README documents
// this as a known limitation), so unlike quest status this can never be
// auto-detected and must be set manually.
public class HideoutRepository
{
    // See QuestRepository.CacheSchemaVersion for why this exists: bump
    // whenever HideoutStation's shape changes in a way that adds fields
    // old cached JSON won't have.
    private const int CacheSchemaVersion = 1;

    private readonly JsonTarkovDevClient _client;
    private readonly string _cacheFilePath;
    private readonly string _levelsFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private class CacheEnvelope
    {
        public int SchemaVersion { get; set; }
        public List<HideoutStation> Stations { get; set; } = new();
    }

    public HideoutRepository(JsonTarkovDevClient client, string appDataFolder)
    {
        _client = client;
        Directory.CreateDirectory(appDataFolder);
        _cacheFilePath = Path.Combine(appDataFolder, "hideout-cache.json");
        _levelsFilePath = Path.Combine(appDataFolder, "hideout-levels.json");
    }

    public async Task<List<HideoutStation>> LoadStationsAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        List<HideoutStation>? stations = null;

        if (!forceRefresh && File.Exists(_cacheFilePath))
        {
            try
            {
                var cached = await File.ReadAllTextAsync(_cacheFilePath, ct);
                var envelope = JsonSerializer.Deserialize<CacheEnvelope>(cached);
                if (envelope?.SchemaVersion == CacheSchemaVersion)
                {
                    stations = envelope.Stations;
                }
            }
            catch (JsonException)
            {
                stations = null;
            }
        }

        if (stations is null)
        {
            stations = await _client.GetHideoutStationsAsync(ct: ct);
            var envelope = new CacheEnvelope { SchemaVersion = CacheSchemaVersion, Stations = stations };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            await File.WriteAllTextAsync(_cacheFilePath, json, ct);
        }

        ApplyLocalLevels(stations);
        return stations;
    }

    private void ApplyLocalLevels(List<HideoutStation> stations)
    {
        var levels = LoadLevels();
        foreach (var station in stations)
        {
            station.CurrentLevel = levels.TryGetValue(station.Id, out var level) ? level : 0;
        }
    }

    private Dictionary<string, int> LoadLevels()
    {
        if (!File.Exists(_levelsFilePath))
        {
            return new Dictionary<string, int>();
        }

        try
        {
            var json = File.ReadAllText(_levelsFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>();
        }
    }

    public void SetStationLevel(string stationId, int level)
    {
        var levels = LoadLevels();
        if (level <= 0)
        {
            levels.Remove(stationId);
        }
        else
        {
            levels[stationId] = level;
        }

        File.WriteAllText(_levelsFilePath, JsonSerializer.Serialize(levels, JsonOptions));
    }
}
