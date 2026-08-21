using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.JsonFallback;

// Fallback data source for when api.tarkov.dev/graphql is down (a real,
// extended outage - see the-hideout/tarkov-api#474). json.tarkov.dev is a
// static Cloudflare R2-backed REST mirror that stays up independently -
// tarkov.dev's own website is built on it, not the GraphQL API.
//
// Quirks verified against real live responses, not assumed:
// - Endpoints return raw internal IDs in "name"/"description" fields
//   (e.g. "<id> name") instead of real text - actual localized strings live
//   in a SEPARATE sibling file at {endpoint}_{langCode} (e.g. "tasks_en"),
//   as a flat dictionary keyed by those exact placeholder strings.
// - Query params/headers for language selection do nothing - it's static
//   file storage, not a dynamic API.
// - Trader/item references inside tasks are bare ID strings, not nested
//   objects - a separate traders/items fetch + translation pass is needed
//   to resolve names.
public class JsonTarkovDevClient
{
    private const string BaseUrl = "https://json.tarkov.dev";
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public JsonTarkovDevClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
    }

    // gameMode is typed (unlike the other Get*Async methods below, which
    // still take the raw "regular"/"pve" URL-path string) so QuestRepository
    // can call this and TarkovDevClient.GetTasksAsync interchangeably with
    // the same GameMode value, without either caller needing to know the
    // underlying JSON-mirror endpoints use lowercase string path segments.
    public Task<List<QuestTask>> GetTasksAsync(GameMode gameMode, CancellationToken ct = default) =>
        GetTasksAsync(gameMode == GameMode.Pve ? "pve" : "regular", ct);

    public async Task<List<QuestTask>> GetTasksAsync(string gameMode = "regular", CancellationToken ct = default)
    {
        var tasksJson = await FetchJsonAsync($"{BaseUrl}/{gameMode}/tasks", ct);
        var translations = await FetchTranslationDictionaryAsync($"{BaseUrl}/{gameMode}/tasks_en", ct);
        var traderNames = await FetchTraderNamesAsync(gameMode, ct);
        var itemNames = await FetchItemNamesAsync(gameMode, ct);
        var mapNames = await FetchMapNormalizedNamesAsync(gameMode, ct);

        return ParseTasks(tasksJson, translations, traderNames, itemNames, mapNames);
    }

    // Full item ID->name catalog (~5000+ entries) - used internally to
    // resolve item names inside tasks/hideout, but also exposed directly
    // for callers that need the whole universe of real item names (e.g.
    // fuzzy-matching noisy OCR text against every known item, not just
    // items that happen to appear in quest/hideout data).
    public Task<Dictionary<string, string>> GetItemNamesAsync(string gameMode = "regular", CancellationToken ct = default) =>
        FetchItemNamesAsync(gameMode, ct);

    // Full item catalog with flea/trader price data, keyed by item ID - a
    // separate fetch from GetItemNamesAsync since /items (the base data
    // file) carries basePrice/avg24hPrice/sellToTrader directly, unlike
    // /items_en which only carries translated display strings.
    public async Task<Dictionary<string, ItemDetails>> GetItemDetailsAsync(string gameMode = "regular", CancellationToken ct = default)
    {
        var itemsJson = await FetchJsonAsync($"{BaseUrl}/{gameMode}/items", ct);
        var translations = await FetchTranslationDictionaryAsync($"{BaseUrl}/{gameMode}/items_en", ct);
        var traderNames = await FetchTraderNamesAsync(gameMode, ct);

        return ParseItemDetails(itemsJson, translations, traderNames);
    }

    // Internal (not private) so tests can feed captured JSON fixtures directly.
    internal static Dictionary<string, ItemDetails> ParseItemDetails(
        JsonElement itemsRoot, IReadOnlyDictionary<string, string> translations, IReadOnlyDictionary<string, string> traderNames)
    {
        var result = new Dictionary<string, ItemDetails>();

        if (!itemsRoot.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("items", out var itemsElement))
        {
            return result;
        }

        foreach (var itemProperty in itemsElement.EnumerateObject())
        {
            var dto = itemProperty.Value.Deserialize<JsonItemDto>(JsonOptions);
            if (dto?.Id is null)
            {
                continue;
            }

            var name = dto.Name is not null && translations.TryGetValue(dto.Name, out var translatedName)
                ? translatedName
                : dto.Id;

            TraderOffer? bestOffer = null;
            foreach (var offer in dto.SellToTrader ?? new List<JsonSellToTraderDto>())
            {
                if (offer.PriceRub is not { } priceRub || offer.Trader is null)
                {
                    continue;
                }

                if (bestOffer is null || priceRub > bestOffer.PriceRub)
                {
                    var traderName = traderNames.TryGetValue(offer.Trader, out var resolvedName) ? resolvedName : offer.Trader;
                    bestOffer = new TraderOffer { TraderName = traderName, PriceRub = priceRub };
                }
            }

            result[dto.Id] = new ItemDetails
            {
                Id = dto.Id,
                Name = name,
                FleaPriceRub = dto.Avg24hPrice,
                BestTraderOffer = bestOffer,
            };
        }

        return result;
    }

    // Ammo has no dedicated endpoint on json.tarkov.dev (unlike GraphQL's
    // separate `ammo` query) - verified directly against a live fetch of
    // /regular/items: ballistic data lives inside each item's own
    // "properties" object, tagged propertiesType == "ItemPropertiesAmmo".
    // Filtering the full item catalog by that tag is the only way to get
    // "every ammo round" from this data source.
    public async Task<List<Ammo>> GetAmmoAsync(string gameMode = "regular", CancellationToken ct = default)
    {
        var itemsJson = await FetchJsonAsync($"{BaseUrl}/{gameMode}/items", ct);
        var translations = await FetchTranslationDictionaryAsync($"{BaseUrl}/{gameMode}/items_en", ct);
        var traderNames = await FetchTraderNamesAsync(gameMode, ct);
        var taskNames = await FetchTaskNamesAsync(gameMode, ct);

        return ParseAmmo(itemsJson, translations, traderNames, taskNames);
    }

    // Internal (not private) so tests can feed captured JSON fixtures directly.
    internal static List<Ammo> ParseAmmo(
        JsonElement itemsRoot,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, string> traderNames,
        IReadOnlyDictionary<string, string> taskNames)
    {
        var result = new List<Ammo>();

        if (!itemsRoot.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("items", out var itemsElement))
        {
            return result;
        }

        foreach (var itemProperty in itemsElement.EnumerateObject())
        {
            var dto = itemProperty.Value.Deserialize<JsonAmmoItemDto>(JsonOptions);
            if (dto?.Id is null || dto.Properties?.PropertiesType != "ItemPropertiesAmmo")
            {
                continue;
            }

            var props = dto.Properties;
            var name = dto.Name is not null && translations.TryGetValue(dto.Name, out var translatedName) ? translatedName : dto.Id;
            var shortName = dto.ShortName is not null && translations.TryGetValue(dto.ShortName, out var translatedShortName) ? translatedShortName : name;

            var offers = new List<AmmoTraderOffer>();
            foreach (var offer in dto.BuyFromTrader ?? new List<JsonBuyFromTraderDto>())
            {
                if (offer.PriceRub is not { } priceRub || offer.Trader is null || offer.MinTraderLevel is not { } loyaltyLevel)
                {
                    continue;
                }

                var traderName = traderNames.TryGetValue(offer.Trader, out var resolvedTraderName) ? resolvedTraderName : offer.Trader;
                var questName = offer.TaskUnlock is not null && taskNames.TryGetValue(offer.TaskUnlock, out var resolvedTaskName)
                    ? resolvedTaskName
                    : null;

                offers.Add(new AmmoTraderOffer
                {
                    TraderName = traderName,
                    LoyaltyLevel = loyaltyLevel,
                    PriceRub = priceRub,
                    RequiredQuestName = questName,
                });
            }

            result.Add(new Ammo
            {
                Id = dto.Id,
                Name = name,
                ShortName = shortName,
                Caliber = props.Caliber ?? "Unknown",
                Damage = props.Damage,
                ArmorDamage = props.ArmorDamage,
                PenetrationPower = props.PenetrationPower,
                FragmentationChance = props.FragmentationChance,
                RicochetChance = props.RicochetChance,
                InitialSpeed = props.InitialSpeed,
                AccuracyModifier = props.AccuracyModifier,
                RecoilModifier = props.RecoilModifier,
                Tracer = props.Tracer,
                TraderOffers = offers,
            });
        }

        return result;
    }

    // Task translation keys use "<id> name" (lowercase) - same convention
    // GetTasksAsync's own translation pass relies on (see
    // FetchTranslationDictionaryAsync's call site in GetTasksAsync),
    // confirmed again here against a live fetch of tasks_en (a taskUnlock
    // ID resolved to "5a68665c86f774255929b4c7 name" -> a real quest name).
    private async Task<Dictionary<string, string>> FetchTaskNamesAsync(string gameMode, CancellationToken ct)
    {
        var translations = await FetchTranslationDictionaryAsync($"{BaseUrl}/{gameMode}/tasks_en", ct);

        var result = new Dictionary<string, string>();
        foreach (var (key, value) in translations)
        {
            if (key.EndsWith(" name", StringComparison.Ordinal))
            {
                var id = key[..^" name".Length];
                result[id] = value;
            }
        }

        return result;
    }

    public async Task<List<HideoutStation>> GetHideoutStationsAsync(string gameMode = "regular", CancellationToken ct = default)
    {
        var hideoutJson = await FetchJsonAsync($"{BaseUrl}/{gameMode}/hideout", ct);
        var translations = await FetchTranslationDictionaryAsync($"{BaseUrl}/{gameMode}/hideout_en", ct);
        var traderNames = await FetchTraderNamesAsync(gameMode, ct);
        var itemNames = await FetchItemNamesAsync(gameMode, ct);

        return ParseHideoutStations(hideoutJson, translations, traderNames, itemNames);
    }

    // Internal (not private) so tests can feed captured JSON fixtures directly.
    internal static List<HideoutStation> ParseHideoutStations(
        JsonElement hideoutRoot,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, string> traderNames,
        IReadOnlyDictionary<string, string> itemNames)
    {
        var result = new List<HideoutStation>();

        if (!hideoutRoot.TryGetProperty("data", out var dataElement))
        {
            return result;
        }

        string Translate(string key) => translations.TryGetValue(key, out var text) ? text : key;

        var stationDtos = new Dictionary<string, JsonHideoutStationDto>();
        foreach (var stationProperty in dataElement.EnumerateObject())
        {
            var dto = stationProperty.Value.Deserialize<JsonHideoutStationDto>(JsonOptions);
            if (dto?.Id is not null)
            {
                stationDtos[dto.Id] = dto;
            }
        }

        // Station-level requirements (e.g. "Workbench needs Security level
        // 2") reference other stations by ID - resolve names using the
        // same batch, now that every station DTO is loaded.
        //
        // Unlike tasks/items/traders (where the translation KEY is built as
        // "<id> <suffix>"), hideout stations' own "name" field already IS
        // the translation key verbatim (e.g. "hideout_area_13_name" ->
        // "Library") - verified against a live fetch. Constructing a key
        // from the ID instead (matching the other endpoints' pattern) looks
        // plausible but silently resolves to nothing, since no such key
        // exists in the translation dictionary.
        string? ResolveStationName(string? stationId) =>
            stationId is not null && stationDtos.TryGetValue(stationId, out var s) && s.Name is not null
                ? Translate(s.Name)
                : null;

        foreach (var dto in stationDtos.Values)
        {
            var station = new HideoutStation
            {
                Id = dto.Id!,
                Name = dto.Name is not null ? Translate(dto.Name) : dto.Id!,
                NormalizedName = dto.NormalizedName ?? string.Empty,
                Levels = (dto.Levels ?? new List<JsonHideoutLevelDto>()).Select(l => new HideoutLevel
                {
                    Level = l.Level,
                    ItemRequirements = (l.ItemRequirements ?? new List<JsonHideoutItemRequirementDto>())
                        .Where(r => r.Item is not null)
                        .Select(r => new HideoutItemRequirement
                        {
                            Item = new Item
                            {
                                Id = r.Item,
                                Name = itemNames.TryGetValue(r.Item!, out var itemName) ? itemName : r.Item!,
                            },
                            Count = r.Count,
                            FoundInRaid = r.Attributes?.FoundInRaid ?? false,
                        }).ToList(),
                    StationRequirements = (l.StationLevelRequirements ?? new List<JsonHideoutStationRequirementDto>())
                        .Select(r => new HideoutStationRequirement
                        {
                            StationId = r.Station,
                            StationName = ResolveStationName(r.Station),
                            Level = r.Level,
                        }).ToList(),
                    TraderRequirements = (l.TraderRequirements ?? new List<JsonHideoutTraderRequirementDto>())
                        .Where(r => r.Trader is not null)
                        .Select(r => new HideoutTraderRequirement
                        {
                            TraderName = traderNames.TryGetValue(r.Trader!, out var traderName) ? traderName : r.Trader!,
                            Level = r.Value,
                        }).ToList(),
                }).ToList(),
            };

            result.Add(station);
        }

        return result;
    }

    // Ground Zero has three separate map entries in /maps by player-level
    // bracket (Sandbox/Sandbox_high/Sandbox_start -> ground-zero/
    // ground-zero-21/ground-zero-tutorial - verified against live data),
    // but only ONE calibration entry exists in tarkov-dev's maps.json
    // ("ground-zero") and MapNameResolver always resolves the current map
    // to that single name regardless of player level. Left unnormalized,
    // objective zones tagged "ground-zero-21" would never match the
    // "ground-zero" the map window is actually showing, silently dropping
    // markers for higher-level players - not a hypothetical, this
    // reproduced for a real user.
    // Same issue as Ground Zero above: Factory has a separate map ID/
    // normalizedName for the night variant ("night-factory") in quest data,
    // but only one "factory" calibration exists and MapNameResolver
    // collapses both factory4_day and factory4_night to "factory" - left
    // unaliased, every Factory-night-tagged objective zone (verified: 14
    // real quests, 59 zone entries) silently never renders on either
    // Factory raid variant.
    private static readonly Dictionary<string, string> NormalizedNameAliases = new(StringComparer.Ordinal)
    {
        ["ground-zero-21"] = "ground-zero",
        ["ground-zero-tutorial"] = "ground-zero",
        ["night-factory"] = "factory",
    };

    // Objective zones reference maps by raw internal ID (e.g.
    // "653e6760052c01c1c805532f"), not normalizedName - resolve via the
    // same /maps endpoint used for extracts.
    private async Task<Dictionary<string, string>> FetchMapNormalizedNamesAsync(string gameMode, CancellationToken ct)
    {
        var mapsJson = await FetchJsonAsync($"{BaseUrl}/{gameMode}/maps", ct);
        return ParseMapNormalizedNames(mapsJson);
    }

    // Internal (not private) so tests can feed captured JSON fixtures directly.
    internal static Dictionary<string, string> ParseMapNormalizedNames(JsonElement mapsRoot)
    {
        var result = new Dictionary<string, string>();

        if (!mapsRoot.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("maps", out var mapsElement))
        {
            return result;
        }

        foreach (var mapProperty in mapsElement.EnumerateObject())
        {
            if (mapProperty.Value.TryGetProperty("normalizedName", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String)
            {
                var normalizedName = nameElement.GetString()!;
                result[mapProperty.Name] = NormalizedNameAliases.GetValueOrDefault(normalizedName, normalizedName);
            }
        }

        return result;
    }

    // Extracts live in the /maps endpoint (not maps.json's calibration
    // data, which has no extract info at all) - keyed by map normalizedName
    // so callers can look up "extracts for the current map" directly.
    public async Task<Dictionary<string, List<MapExtract>>> GetExtractsByMapAsync(
        string gameMode = "regular", CancellationToken ct = default)
    {
        var mapsJson = await FetchJsonAsync($"{BaseUrl}/{gameMode}/maps", ct);
        var translations = await FetchTranslationDictionaryAsync($"{BaseUrl}/{gameMode}/maps_en", ct);

        return ParseExtracts(mapsJson, translations);
    }

    // Loot container spawn points live in the same /maps endpoint as
    // extracts, plus a separate top-level container-type catalog
    // (data.lootContainers, keyed by container id) that gives each
    // container instance its display name/normalizedName - a "safe"
    // spawn point only carries the container id, not its type info.
    public async Task<Dictionary<string, List<LootContainerSpawn>>> GetLootContainersByMapAsync(
        string gameMode = "regular", CancellationToken ct = default)
    {
        var mapsJson = await FetchJsonAsync($"{BaseUrl}/{gameMode}/maps", ct);
        var translations = await FetchTranslationDictionaryAsync($"{BaseUrl}/{gameMode}/maps_en", ct);

        return ParseLootContainers(mapsJson, translations);
    }

    // Internal (not private) so tests can feed captured JSON fixtures directly.
    internal static Dictionary<string, List<LootContainerSpawn>> ParseLootContainers(
        JsonElement mapsRoot, IReadOnlyDictionary<string, string> translations)
    {
        var result = new Dictionary<string, List<LootContainerSpawn>>();

        if (!mapsRoot.TryGetProperty("data", out var dataElement))
        {
            return result;
        }

        var containerTypes = new Dictionary<string, JsonLootContainerTypeDto>();
        if (dataElement.TryGetProperty("lootContainers", out var containerTypesElement))
        {
            foreach (var property in containerTypesElement.EnumerateObject())
            {
                var typeDto = property.Value.Deserialize<JsonLootContainerTypeDto>(JsonOptions);
                if (typeDto is not null)
                {
                    containerTypes[property.Name] = typeDto;
                }
            }
        }

        if (!dataElement.TryGetProperty("maps", out var mapsElement))
        {
            return result;
        }

        string Translate(string key) => translations.TryGetValue(key, out var text) ? text : key;

        foreach (var mapProperty in mapsElement.EnumerateObject())
        {
            var dto = mapProperty.Value.Deserialize<JsonMapDto>(JsonOptions);
            if (dto?.NormalizedName is null)
            {
                continue;
            }

            var spawns = (dto.LootContainers ?? new List<JsonLootContainerPositionDto>())
                .Where(c => c.Position is not null && c.LootContainer is not null)
                .Select(c =>
                {
                    var containerId = c.LootContainer!;
                    var typeDto = containerTypes.GetValueOrDefault(containerId);
                    var normalizedName = typeDto?.NormalizedName ?? containerId;

                    // Fall back to normalizedName (e.g. "safe"), not the raw
                    // translation key (e.g. "<id> Name") - unlike extracts,
                    // which have no better fallback than their raw name,
                    // container types always have a normalizedName available.
                    var name = typeDto?.Name is not null && translations.ContainsKey(typeDto.Name)
                        ? Translate(typeDto.Name)
                        : normalizedName;

                    return new LootContainerSpawn
                    {
                        ContainerNormalizedName = normalizedName,
                        ContainerName = name,
                        X = c.Position!.X,
                        Z = c.Position.Z,
                    };
                })
                .ToList();

            result[dto.NormalizedName] = spawns;
        }

        return result;
    }

    // Internal (not private) so tests can feed captured JSON fixtures directly.
    internal static Dictionary<string, List<MapExtract>> ParseExtracts(
        JsonElement mapsRoot, IReadOnlyDictionary<string, string> translations)
    {
        var result = new Dictionary<string, List<MapExtract>>();

        if (!mapsRoot.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("maps", out var mapsElement))
        {
            return result;
        }

        string Translate(string key) => translations.TryGetValue(key, out var text) ? text : key;

        foreach (var mapProperty in mapsElement.EnumerateObject())
        {
            var dto = mapProperty.Value.Deserialize<JsonMapDto>(JsonOptions);
            if (dto?.NormalizedName is null)
            {
                continue;
            }

            var extracts = (dto.Extracts ?? new List<JsonExtractDto>())
                .Where(e => e.Position is not null)
                .Select(e => new MapExtract
                {
                    Id = e.Id ?? string.Empty,
                    Name = e.Name is not null ? Translate(e.Name) : string.Empty,
                    Faction = e.Faction ?? "shared",
                    X = e.Position!.X,
                    Z = e.Position.Z,
                })
                .ToList();

            result[dto.NormalizedName] = extracts;
        }

        return result;
    }

    // Internal (not private) so tests can feed captured JSON fixtures directly.
    internal static List<QuestTask> ParseTasks(
        JsonElement tasksRoot,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, string> traderNames,
        IReadOnlyDictionary<string, string> itemNames,
        IReadOnlyDictionary<string, string>? mapNames = null)
    {
        mapNames ??= new Dictionary<string, string>();
        var result = new List<QuestTask>();

        if (!tasksRoot.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("tasks", out var tasksElement))
        {
            return result;
        }

        string Translate(string key) => translations.TryGetValue(key, out var text) ? text : key;

        foreach (var taskProperty in tasksElement.EnumerateObject())
        {
            var dto = taskProperty.Value.Deserialize<JsonTaskDto>(JsonOptions);
            if (dto is null)
            {
                continue;
            }

            var task = new QuestTask
            {
                Id = dto.Id,
                Name = Translate($"{dto.Id} name"),
                Trader = new Trader
                {
                    Id = dto.Trader,
                    Name = dto.Trader is not null && traderNames.TryGetValue(dto.Trader, out var traderName)
                        ? traderName
                        : dto.Trader ?? string.Empty,
                },
                Map = BuildTaskMap(dto.Map, mapNames),
                MinPlayerLevel = dto.MinPlayerLevel,
                WikiLink = dto.WikiLink,
                TaskRequirements = (dto.TaskRequirements ?? new List<JsonTaskRequirementDto>()).Select(r =>
                    new TaskStatusRequirement
                    {
                        Task = new TaskRef { Id = r.Task },
                        Status = r.Status ?? new List<string>(),
                    }).ToList(),
                Objectives = (dto.Objectives ?? new List<JsonObjectiveDto>()).Select(o => new TaskObjective
                {
                    Id = o.Id,
                    Type = o.Type ?? string.Empty,
                    Description = o.Description is not null ? Translate(o.Description) : string.Empty,
                    Optional = o.Optional,
                    Count = o.Count,
                    FoundInRaid = o.FoundInRaid,
                    Items = (o.Items ?? new List<string>()).Select(itemId => new Item
                    {
                        Id = itemId,
                        Name = itemNames.TryGetValue(itemId, out var itemName) ? itemName : itemId,
                    }).ToList(),
                    Zones = BuildZones(o, mapNames),
                }).ToList(),
            };

            result.Add(task);
        }

        return result;
    }

    // Resolves a task's top-level map ID (e.g. "653e6760052c01c1c805532f")
    // to a GameMap, or null for maps that apply to any/no specific map
    // (raw "map" field absent - some tasks genuinely aren't tied to one
    // map). No separate maps_en translation fetch for the display name -
    // title-casing the already-available normalizedName ("ground-zero" ->
    // "Ground Zero") avoids adding another network round-trip just for
    // this one column, and produces the same reasonable display text.
    private static GameMap? BuildTaskMap(string? mapId, IReadOnlyDictionary<string, string> mapNames)
    {
        if (mapId is null || !mapNames.TryGetValue(mapId, out var normalizedName))
        {
            return null;
        }

        return new GameMap
        {
            Id = mapId,
            NormalizedName = normalizedName,
            Name = TitleCaseNormalizedName(normalizedName),
        };
    }

    private static string TitleCaseNormalizedName(string normalizedName) =>
        string.Join(' ', normalizedName.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    // Combines two distinct position sources into one unified Zones list:
    // "zones" (a single fixed spot) and, for findQuestItem objectives only,
    // "possibleLocations" (multiple candidate spawn points, since the
    // actual item spawns randomly at one of several places each raid -
    // verified: only findQuestItem uses this field, 113 objectives across
    // the full dataset). Both represent "where might this objective be",
    // so callers that just want "candidate positions for this objective"
    // don't need to know which underlying field they came from.
    private static List<ObjectiveZone> BuildZones(JsonObjectiveDto o, IReadOnlyDictionary<string, string> mapNames)
    {
        var zones = (o.Zones ?? new List<JsonZoneDto>())
            .Where(z => z.Position is not null && z.Map is not null)
            .Select(z => new ObjectiveZone
            {
                MapNormalizedName = mapNames.TryGetValue(z.Map!, out var mapName) ? mapName : null,
                X = z.Position!.X,
                Z = z.Position.Z,
            });

        var possibleLocations = (o.PossibleLocations ?? new List<JsonPossibleLocationDto>())
            .Where(pl => pl.Map is not null)
            .SelectMany(pl => (pl.Positions ?? new List<JsonPositionDto>())
                .Select(pos => new ObjectiveZone
                {
                    MapNormalizedName = mapNames.TryGetValue(pl.Map!, out var mapName) ? mapName : null,
                    X = pos.X,
                    Z = pos.Z,
                }));

        // Raw data sometimes repeats the exact same zone entry verbatim
        // (verified: real "Work Smarter" objective data has the same
        // Customs position listed twice) - dedupe so clustering/centroid
        // math isn't skewed by a duplicate weighting one position higher
        // than a genuinely distinct one.
        return zones.Concat(possibleLocations)
            .Where(z => z.MapNormalizedName is not null)
            .DistinctBy(z => (z.MapNormalizedName, z.X, z.Z))
            .ToList();
    }

    private async Task<Dictionary<string, string>> FetchTraderNamesAsync(string gameMode, CancellationToken ct)
    {
        var tradersJson = await FetchJsonAsync($"{BaseUrl}/{gameMode}/traders", ct);
        var translations = await FetchTranslationDictionaryAsync($"{BaseUrl}/{gameMode}/traders_en", ct);

        var result = new Dictionary<string, string>();
        if (!tradersJson.TryGetProperty("data", out var dataElement))
        {
            return result;
        }

        foreach (var traderProperty in dataElement.EnumerateObject())
        {
            var id = traderProperty.Name;
            var nicknameKey = $"{id} Nickname";
            result[id] = translations.TryGetValue(nicknameKey, out var name) ? name : id;
        }

        return result;
    }

    private async Task<Dictionary<string, string>> FetchItemNamesAsync(string gameMode, CancellationToken ct)
    {
        var translations = await FetchTranslationDictionaryAsync($"{BaseUrl}/{gameMode}/items_en", ct);

        // Item translation keys use "<id> Name" (capital N) - verified
        // against a live fetch. This differs from tasks' "<id> name"
        // (lowercase) - the suffix casing is not a uniform convention
        // across endpoints and must not be assumed.
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in translations)
        {
            if (key.EndsWith(" Name", StringComparison.Ordinal))
            {
                var id = key[..^" Name".Length];
                result[id] = value;
            }
        }

        return result;
    }

    private async Task<JsonElement> FetchJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    private async Task<Dictionary<string, string>> FetchTranslationDictionaryAsync(string url, CancellationToken ct)
    {
        var root = await FetchJsonAsync(url, ct);
        return ParseTranslationDictionary(root);
    }

    internal static Dictionary<string, string> ParseTranslationDictionary(JsonElement root)
    {
        var result = new Dictionary<string, string>();
        if (!root.TryGetProperty("data", out var dataElement))
        {
            return result;
        }

        foreach (var property in dataElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString() ?? property.Name;
            }
        }

        return result;
    }

    private class JsonTaskDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("trader")]
        public string? Trader { get; set; }

        [JsonPropertyName("wikiLink")]
        public string? WikiLink { get; set; }

        [JsonPropertyName("minPlayerLevel")]
        public int? MinPlayerLevel { get; set; }

        [JsonPropertyName("taskRequirements")]
        public List<JsonTaskRequirementDto>? TaskRequirements { get; set; }

        [JsonPropertyName("objectives")]
        public List<JsonObjectiveDto>? Objectives { get; set; }

        // Raw map ID (e.g. "653e6760052c01c1c805532f"), not a normalizedName
        // or nested object - same raw-ID shape as objective zones' "map"
        // field, resolved via the same mapNames lookup. Real bug: this
        // property didn't exist at all until now, so System.Text.Json
        // silently dropped the raw JSON's "map" field on every task and
        // QuestTask.Map was never populated - the quest grid's Map column
        // showed "Any map" (its null-fallback text) for every single quest
        // regardless of the real data.
        [JsonPropertyName("map")]
        public string? Map { get; set; }
    }

    private class JsonTaskRequirementDto
    {
        [JsonPropertyName("task")]
        public string? Task { get; set; }

        [JsonPropertyName("status")]
        public List<string>? Status { get; set; }
    }

    private class JsonObjectiveDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("optional")]
        public bool Optional { get; set; }

        [JsonPropertyName("count")]
        public int? Count { get; set; }

        [JsonPropertyName("foundInRaid")]
        public bool? FoundInRaid { get; set; }

        [JsonPropertyName("items")]
        public List<string>? Items { get; set; }

        [JsonPropertyName("zones")]
        public List<JsonZoneDto>? Zones { get; set; }

        [JsonPropertyName("possibleLocations")]
        public List<JsonPossibleLocationDto>? PossibleLocations { get; set; }
    }

    private class JsonPossibleLocationDto
    {
        [JsonPropertyName("map")]
        public string? Map { get; set; }

        [JsonPropertyName("positions")]
        public List<JsonPositionDto>? Positions { get; set; }
    }

    private class JsonZoneDto
    {
        [JsonPropertyName("map")]
        public string? Map { get; set; }

        [JsonPropertyName("position")]
        public JsonPositionDto? Position { get; set; }
    }

    private class JsonMapDto
    {
        [JsonPropertyName("normalizedName")]
        public string? NormalizedName { get; set; }

        [JsonPropertyName("extracts")]
        public List<JsonExtractDto>? Extracts { get; set; }

        [JsonPropertyName("lootContainers")]
        public List<JsonLootContainerPositionDto>? LootContainers { get; set; }
    }

    private class JsonLootContainerPositionDto
    {
        // References an entry in the separate top-level lootContainers
        // catalog (container TYPE info) by id - a spawn point itself only
        // carries which container id occupies it, not its name/type.
        [JsonPropertyName("lootContainer")]
        public string? LootContainer { get; set; }

        [JsonPropertyName("position")]
        public JsonPositionDto? Position { get; set; }
    }

    private class JsonLootContainerTypeDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        // Literal translation key (e.g. "578f87a3245977356274f2cb Name"),
        // same "<id> Name" pattern as items - resolved via maps_en.
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("normalizedName")]
        public string? NormalizedName { get; set; }
    }

    private class JsonExtractDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("faction")]
        public string? Faction { get; set; }

        [JsonPropertyName("position")]
        public JsonPositionDto? Position { get; set; }
    }

    private class JsonPositionDto
    {
        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("z")]
        public float Z { get; set; }
    }

    private class JsonHideoutStationDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        // The literal translation KEY (e.g. "hideout_area_13_name"), not
        // display text and not an ID-derived lookup key like other endpoints.
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("normalizedName")]
        public string? NormalizedName { get; set; }

        [JsonPropertyName("levels")]
        public List<JsonHideoutLevelDto>? Levels { get; set; }
    }

    private class JsonHideoutLevelDto
    {
        [JsonPropertyName("level")]
        public int Level { get; set; }

        [JsonPropertyName("itemRequirements")]
        public List<JsonHideoutItemRequirementDto>? ItemRequirements { get; set; }

        [JsonPropertyName("stationLevelRequirements")]
        public List<JsonHideoutStationRequirementDto>? StationLevelRequirements { get; set; }

        [JsonPropertyName("traderRequirements")]
        public List<JsonHideoutTraderRequirementDto>? TraderRequirements { get; set; }
    }

    private class JsonHideoutItemRequirementDto
    {
        [JsonPropertyName("item")]
        public string? Item { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("attributes")]
        public JsonHideoutItemAttributesDto? Attributes { get; set; }
    }

    private class JsonHideoutItemAttributesDto
    {
        [JsonPropertyName("foundInRaid")]
        public bool? FoundInRaid { get; set; }
    }

    private class JsonHideoutStationRequirementDto
    {
        [JsonPropertyName("station")]
        public string? Station { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    private class JsonHideoutTraderRequirementDto
    {
        [JsonPropertyName("trader")]
        public string? Trader { get; set; }

        // "value" is the level threshold for requirementType="level"
        // (verified against real data: {"requirementType":"level",
        // "compareMethod":">=","value":2,"trader":"..."}).
        [JsonPropertyName("value")]
        public int Value { get; set; }
    }

    private class JsonItemDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        // Literal translation key (e.g. "<id> Name"), resolved via items_en -
        // same pattern as tasks/hideout, "<id> Name" casing verified live.
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("avg24hPrice")]
        public int? Avg24hPrice { get; set; }

        [JsonPropertyName("sellToTrader")]
        public List<JsonSellToTraderDto>? SellToTrader { get; set; }
    }

    private class JsonSellToTraderDto
    {
        [JsonPropertyName("trader")]
        public string? Trader { get; set; }

        // Pre-converted to RUB regardless of the offer's real currency
        // (some traders pay in USD/EUR) - using this instead of "price"
        // avoids needing a currency conversion step to compare offers.
        [JsonPropertyName("priceRUB")]
        public int? PriceRub { get; set; }
    }

    // Separate from JsonItemDto - ammo needs BUY offers (buyFromTrader),
    // not sell offers, plus the ballistic properties block. Fields verified
    // against a real live fetch of https://json.tarkov.dev/regular/items
    // (a 5.56x45mm M855 entry): damage/armorDamage/penetrationPower are
    // duplicated both flattened on the item root AND nested under
    // "properties" - read from properties, since that's guaranteed to only
    // be present on genuine ammo items (propertiesType ==
    // "ItemPropertiesAmmo"), unlike the flattened root fields which could
    // coincidentally collide with another item type's own same-named field.
    private class JsonAmmoItemDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("shortName")]
        public string? ShortName { get; set; }

        [JsonPropertyName("properties")]
        public JsonAmmoPropertiesDto? Properties { get; set; }

        [JsonPropertyName("buyFromTrader")]
        public List<JsonBuyFromTraderDto>? BuyFromTrader { get; set; }
    }

    private class JsonAmmoPropertiesDto
    {
        [JsonPropertyName("propertiesType")]
        public string? PropertiesType { get; set; }

        [JsonPropertyName("caliber")]
        public string? Caliber { get; set; }

        [JsonPropertyName("damage")]
        public int Damage { get; set; }

        [JsonPropertyName("armorDamage")]
        public int ArmorDamage { get; set; }

        [JsonPropertyName("penetrationPower")]
        public int PenetrationPower { get; set; }

        [JsonPropertyName("fragmentationChance")]
        public double FragmentationChance { get; set; }

        [JsonPropertyName("ricochetChance")]
        public double RicochetChance { get; set; }

        [JsonPropertyName("initialSpeed")]
        public double InitialSpeed { get; set; }

        [JsonPropertyName("accuracyModifier")]
        public double? AccuracyModifier { get; set; }

        [JsonPropertyName("recoilModifier")]
        public double? RecoilModifier { get; set; }

        [JsonPropertyName("tracer")]
        public bool Tracer { get; set; }
    }

    private class JsonBuyFromTraderDto
    {
        [JsonPropertyName("trader")]
        public string? Trader { get; set; }

        // Pre-converted to RUB regardless of the offer's real currency -
        // same rationale as JsonSellToTraderDto.PriceRub.
        [JsonPropertyName("priceRUB")]
        public int? PriceRub { get; set; }

        [JsonPropertyName("minTraderLevel")]
        public int? MinTraderLevel { get; set; }

        // Bare task ID string, not a nested object - verified against a
        // real live fetch (e.g. "taskUnlock": "5a68665c86f774255929b4c7"),
        // resolved against tasks_en separately, same pattern as
        // trader/item ID references elsewhere in this file.
        [JsonPropertyName("taskUnlock")]
        public string? TaskUnlock { get; set; }
    }
}
