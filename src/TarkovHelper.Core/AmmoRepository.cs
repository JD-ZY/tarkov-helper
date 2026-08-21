using System.Text.Json;
using TarkovHelper.Core.JsonFallback;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core;

// Caches ammo ballistic/purchase data locally, same disk-cache-with-schema-
// version pattern as QuestRepository/HideoutRepository - avoids refetching
// ~200+ ammo entries from tarkov.dev on every launch, and survives brief API
// outages by falling back to the last successfully cached data instead of
// showing an empty chart.
//
// Uses JsonTarkovDevClient (the json.tarkov.dev fallback), not the GraphQL
// TarkovDevClient, despite GraphQL having a purpose-built `ammo` query with
// a cleaner shape - api.tarkov.dev/graphql has been down for an extended,
// publicly-tracked outage (the-hideout/tarkov-api#474, open 2+ weeks as of
// this being written) with no fix landed, while json.tarkov.dev was
// confirmed live and returning real ballistic/trader-offer data via a
// direct fetch. Matches every other repository in this app, which already
// standardized on the JSON fallback for exactly this reason.
public class AmmoRepository
{
    // Bump whenever Ammo/AmmoTraderOffer's shape changes in a way that adds
    // fields old cached JSON won't have - see QuestRepository.CacheSchemaVersion
    // for the full rationale (System.Text.Json silently defaults missing
    // fields rather than erroring, so a stale cache doesn't fail, it just
    // quietly omits the new data).
    private const int CacheSchemaVersion = 1;

    private readonly JsonTarkovDevClient _client;
    private readonly string _cacheFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private class CacheEnvelope
    {
        public int SchemaVersion { get; set; }
        public List<Ammo> Ammo { get; set; } = new();
    }

    public AmmoRepository(JsonTarkovDevClient client, string appDataFolder)
    {
        _client = client;
        Directory.CreateDirectory(appDataFolder);
        _cacheFilePath = Path.Combine(appDataFolder, "ammo-cache.json");
    }

    public async Task<List<Ammo>> LoadAmmoAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        List<Ammo>? ammo = null;

        if (!forceRefresh && File.Exists(_cacheFilePath))
        {
            try
            {
                var cached = await File.ReadAllTextAsync(_cacheFilePath, ct);
                var envelope = JsonSerializer.Deserialize<CacheEnvelope>(cached);
                if (envelope?.SchemaVersion == CacheSchemaVersion)
                {
                    ammo = envelope.Ammo;
                }
            }
            catch (JsonException)
            {
                ammo = null;
            }
        }

        if (ammo is null)
        {
            ammo = await _client.GetAmmoAsync(ct: ct);
            var envelope = new CacheEnvelope { SchemaVersion = CacheSchemaVersion, Ammo = ammo };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            await File.WriteAllTextAsync(_cacheFilePath, json, ct);
        }

        return ammo;
    }
}
