using System.Text.Json;
using TarkovHelper.Core.JsonFallback;
using TarkovHelper.Core.Logs;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core;

// Caches tarkov.dev task data locally so the app remains usable offline
// (and so the flaky/rate-limited API only needs to be hit occasionally),
// and persists which tasks the player has marked complete separately from
// the API data, since completion is local-only state.
public class QuestRepository
{
    // Bump whenever QuestTask/TaskObjective's shape changes in a way that
    // adds fields old cached JSON won't have - System.Text.Json silently
    // deserializes missing fields as their default (e.g. empty list) rather
    // than erroring, so a stale cache doesn't fail, it just quietly omits
    // the new data (e.g. objective Zones went missing this way once
    // already, making map objective markers appear broken when the real
    // cause was cached data older than the feature that reads it).
    // v3: TarkovDevClient's GraphQL query never requested zones/
    // possibleLocations at all (only the JSON fallback client did), so
    // every GraphQL-sourced cache had empty Zones on every objective -
    // bumping forces a refetch now that the query and parsing include them.
    // v4: JsonTarkovDevClient's ParseTasks never populated QuestTask.Map at
    // all (JsonTaskDto didn't even declare a "map" property, so
    // System.Text.Json silently dropped the real per-task map ID from the
    // raw JSON) - every task showed "Any map" in the quest grid regardless
    // of its real map. Bumping forces a refetch now that Map is parsed.
    private const int CacheSchemaVersion = 4;

    private readonly TarkovDevClient _client;
    private readonly JsonTarkovDevClient _fallbackClient;
    private readonly string _appDataFolder;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private class CacheEnvelope
    {
        public int SchemaVersion { get; set; }
        public List<QuestTask> Tasks { get; set; } = new();
    }

    public QuestRepository(TarkovDevClient client, string appDataFolder, JsonTarkovDevClient? fallbackClient = null)
    {
        _client = client;
        _fallbackClient = fallbackClient ?? new JsonTarkovDevClient();
        _appDataFolder = appDataFolder;
        Directory.CreateDirectory(appDataFolder);
    }

    // File names are mode-scoped (e.g. "tasks-cache-pve.json" vs
    // "tasks-cache.json") rather than sharing one set of files across modes.
    // This matters for more than just the task cache: PvE and PvP use
    // entirely separate in-game characters with independent quest progress
    // (verified: EFT's own mode-select flow describes PvE as having "its
    // own stash, economy, tasks, and progress") - a single shared
    // quest-progress.json would incorrectly mark a quest complete in PvP
    // because it was finished on the PvE character, or vice versa. Regular
    // keeps the original unsuffixed filenames so existing installs don't
    // lose previously-tracked progress on upgrade.
    private string CacheFilePath(GameMode mode) =>
        Path.Combine(_appDataFolder, mode == GameMode.Pve ? "tasks-cache-pve.json" : "tasks-cache.json");

    private string ProgressFilePath(GameMode mode) =>
        Path.Combine(_appDataFolder, mode == GameMode.Pve ? "quest-progress-pve.json" : "quest-progress.json");

    private string ActiveFilePath(GameMode mode) =>
        Path.Combine(_appDataFolder, mode == GameMode.Pve ? "active-quests-pve.json" : "active-quests.json");

    public async Task<List<QuestTask>> LoadTasksAsync(bool forceRefresh = false, CancellationToken ct = default) =>
        await LoadTasksAsync(GameMode.Regular, forceRefresh, ct);

    public async Task<List<QuestTask>> LoadTasksAsync(GameMode mode, bool forceRefresh = false, CancellationToken ct = default)
    {
        var cacheFilePath = CacheFilePath(mode);
        List<QuestTask>? tasks = null;

        if (!forceRefresh && File.Exists(cacheFilePath))
        {
            try
            {
                var cached = await File.ReadAllTextAsync(cacheFilePath, ct);
                var envelope = JsonSerializer.Deserialize<CacheEnvelope>(cached);
                if (envelope?.SchemaVersion == CacheSchemaVersion)
                {
                    tasks = envelope.Tasks;
                }
                // A schema-mismatched or unversioned (pre-envelope) cache is
                // treated as absent - falls through to a live re-fetch below.
            }
            catch (JsonException)
            {
                tasks = null;
            }
        }

        if (tasks is null)
        {
            tasks = await FetchTasksWithFallbackAsync(mode, ct);
            var envelope = new CacheEnvelope { SchemaVersion = CacheSchemaVersion, Tasks = tasks };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            await File.WriteAllTextAsync(cacheFilePath, json, ct);
        }

        ApplyLocalProgress(tasks, mode);
        return tasks;
    }

    // api.tarkov.dev/graphql has had extended outages (e.g.
    // the-hideout/tarkov-api#474) during which json.tarkov.dev - a static
    // REST/R2 mirror tarkov.dev's own website is built on - stays up. Try
    // the primary GraphQL API first (richer data: item objects with
    // shortName, structured maps, etc.) and only fall back to the JSON
    // mirror if it fails, rather than always preferring the lesser-featured
    // fallback.
    private async Task<List<QuestTask>> FetchTasksWithFallbackAsync(GameMode mode, CancellationToken ct)
    {
        try
        {
            return await _client.GetTasksAsync(mode, ct);
        }
        catch (Exception primaryEx) when (primaryEx is InvalidOperationException or HttpRequestException)
        {
            try
            {
                return await _fallbackClient.GetTasksAsync(mode, ct);
            }
            catch (Exception fallbackEx)
            {
                throw new InvalidOperationException(
                    $"Both tarkov.dev data sources failed. GraphQL: {primaryEx.Message}. JSON fallback: {fallbackEx.Message}",
                    fallbackEx);
            }
        }
    }

    private void ApplyLocalProgress(List<QuestTask> tasks, GameMode mode)
    {
        var completedIds = LoadIdSet(ProgressFilePath(mode));
        var activeIds = LoadIdSet(ActiveFilePath(mode));
        foreach (var task in tasks)
        {
            if (task.Id is not null)
            {
                task.IsComplete = completedIds.Contains(task.Id);
                task.IsActive = activeIds.Contains(task.Id);
            }
        }
    }

    // Locally-tracked active quests (from EFT's own logs) can outrun
    // tarkov.dev's dataset - a brand-new quest's log-reported task ID is
    // real (verified: MongoDB ObjectIds embed a creation timestamp, and a
    // real user's "phantom" active quest IDs decoded to a date after
    // tarkov.dev's last data refresh), it's just not in tarkov.dev's tasks
    // list yet. Such an ID has no QuestTask to attach IsActive to, so it
    // silently never appears anywhere in the UI - this lets callers detect
    // that gap and say something honest ("N active quests not yet in
    // tarkov.dev's data") instead of just dropping them with no trace.
    public int CountActiveIdsMissingFrom(IEnumerable<QuestTask> tasks, GameMode mode = GameMode.Regular)
    {
        var activeIds = LoadIdSet(ActiveFilePath(mode));
        var knownIds = tasks.Select(t => t.Id).Where(id => id is not null).ToHashSet();
        return activeIds.Count(id => !knownIds.Contains(id));
    }

    private static HashSet<string> LoadIdSet(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new HashSet<string>();
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
        }
        catch (JsonException)
        {
            return new HashSet<string>();
        }
    }

    public void SetTaskComplete(string taskId, bool isComplete, GameMode mode = GameMode.Regular)
    {
        var progressFilePath = ProgressFilePath(mode);
        var completedIds = LoadIdSet(progressFilePath);
        if (isComplete)
        {
            completedIds.Add(taskId);
        }
        else
        {
            completedIds.Remove(taskId);
        }

        File.WriteAllText(progressFilePath, JsonSerializer.Serialize(completedIds, JsonOptions));

        // Completing a quest implies it's no longer active.
        if (isComplete)
        {
            SetTaskActive(taskId, false, mode);
        }
    }

    public void SetTaskActive(string taskId, bool isActive, GameMode mode = GameMode.Regular)
    {
        var activeFilePath = ActiveFilePath(mode);
        var activeIds = LoadIdSet(activeFilePath);
        if (isActive)
        {
            activeIds.Add(taskId);
        }
        else
        {
            activeIds.Remove(taskId);
        }

        File.WriteAllText(activeFilePath, JsonSerializer.Serialize(activeIds, JsonOptions));
    }

    // Applies many status transitions (e.g. from GameLogWatcher.ReplayHistory,
    // which can fire hundreds of events for a long quest history) in memory,
    // then writes each output file exactly once - SetTaskComplete/SetTaskActive
    // each do a full read+write per call, which would otherwise mean hundreds
    // of redundant file round-trips for a single startup replay.
    public void ApplyStatusHistory(IEnumerable<(string TaskId, QuestTaskStatus Status)> transitions, GameMode mode = GameMode.Regular)
    {
        var progressFilePath = ProgressFilePath(mode);
        var activeFilePath = ActiveFilePath(mode);
        var completedIds = LoadIdSet(progressFilePath);
        var activeIds = LoadIdSet(activeFilePath);

        foreach (var (taskId, status) in transitions)
        {
            switch (status)
            {
                case QuestTaskStatus.Started:
                    activeIds.Add(taskId);
                    break;
                case QuestTaskStatus.Finished:
                    completedIds.Add(taskId);
                    activeIds.Remove(taskId);
                    break;
                case QuestTaskStatus.Failed:
                    activeIds.Remove(taskId);
                    break;
            }
        }

        File.WriteAllText(progressFilePath, JsonSerializer.Serialize(completedIds, JsonOptions));
        File.WriteAllText(activeFilePath, JsonSerializer.Serialize(activeIds, JsonOptions));
    }
}
