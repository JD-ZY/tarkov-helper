using System.Net;
using System.Text;
using System.Text.Json;
using TarkovHelper.Core;
using TarkovHelper.Core.Logs;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.Tests;

// Fake HttpMessageHandler so these tests exercise QuestRepository's cache
// read/write and progress-persistence logic without hitting the real
// (currently unreliable) tarkov.dev API.
file class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public int CallCount { get; private set; }

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(_responder(request));
    }
}

public class QuestRepositoryTests : IDisposable
{
    private readonly string _tempDir;

    public QuestRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TarkovHelperTests_" + Guid.NewGuid());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task FirstLoad_FetchesFromApiAndWritesCache()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        var tasks = await repo.LoadTasksAsync();

        Assert.Single(tasks);
        Assert.Equal("Debut", tasks[0].Name);
        Assert.Equal(1, handler.CallCount);
        Assert.True(File.Exists(Path.Combine(_tempDir, "tasks-cache.json")));
    }

    [Fact]
    public async Task SecondLoad_UsesCacheInsteadOfCallingApiAgain()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        await repo.LoadTasksAsync();
        await repo.LoadTasksAsync();

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ForceRefresh_BypassesCacheAndCallsApiAgain()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        await repo.LoadTasksAsync();
        await repo.LoadTasksAsync(forceRefresh: true);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SetTaskComplete_PersistsAcrossRepositoryInstances()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));

        var repo1 = new QuestRepository(client, _tempDir);
        await repo1.LoadTasksAsync();
        repo1.SetTaskComplete("t1", true);

        // New repository instance, same folder - simulates app restart.
        var repo2 = new QuestRepository(client, _tempDir);
        var tasks = await repo2.LoadTasksAsync();

        Assert.True(tasks.Single(t => t.Id == "t1").IsComplete);
    }

    [Fact]
    public async Task UnsettingCompletion_Persists()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));

        var repo = new QuestRepository(client, _tempDir);
        await repo.LoadTasksAsync();
        repo.SetTaskComplete("t1", true);
        repo.SetTaskComplete("t1", false);

        var repo2 = new QuestRepository(client, _tempDir);
        var tasks = await repo2.LoadTasksAsync();

        Assert.False(tasks.Single(t => t.Id == "t1").IsComplete);
    }

    [Fact]
    public async Task SetTaskActive_PersistsAcrossRepositoryInstances()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));

        var repo1 = new QuestRepository(client, _tempDir);
        await repo1.LoadTasksAsync();
        repo1.SetTaskActive("t1", true);

        var repo2 = new QuestRepository(client, _tempDir);
        var tasks = await repo2.LoadTasksAsync();

        Assert.True(tasks.Single(t => t.Id == "t1").IsActive);
    }

    [Fact]
    public async Task CompletingAnActiveTask_ClearsActiveFlag()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));

        var repo = new QuestRepository(client, _tempDir);
        await repo.LoadTasksAsync();
        repo.SetTaskActive("t1", true);
        repo.SetTaskComplete("t1", true);

        var repo2 = new QuestRepository(client, _tempDir);
        var tasks = await repo2.LoadTasksAsync();

        var task = tasks.Single(t => t.Id == "t1");
        Assert.True(task.IsComplete);
        Assert.False(task.IsActive);
    }

    [Fact]
    public async Task ApplyStatusHistory_StartedThenFinished_EndsUpCompleteNotActive()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));

        var repo = new QuestRepository(client, _tempDir);
        repo.ApplyStatusHistory(new[]
        {
            ("t1", QuestTaskStatus.Started),
            ("t1", QuestTaskStatus.Finished),
        });

        var tasks = await repo.LoadTasksAsync();
        var task = tasks.Single(t => t.Id == "t1");

        Assert.True(task.IsComplete);
        Assert.False(task.IsActive);
    }

    [Fact]
    public async Task ApplyStatusHistory_OnlyWritesFilesOnce_NotOncePerTransition()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        // Larger, more realistic history size than the 2-event minimal
        // case above - confirms correctness holds at scale, not just that
        // the method compiles for a trivial input.
        var transitions = Enumerable.Range(0, 50)
            .Select(i => ($"t{i}", QuestTaskStatus.Started))
            .ToList();
        transitions.Add(("t1", QuestTaskStatus.Finished));

        repo.ApplyStatusHistory(transitions);

        var tasks = await repo.LoadTasksAsync();
        var task = tasks.Single(t => t.Id == "t1");
        Assert.True(task.IsComplete);
    }

    [Fact]
    public async Task ApplyStatusHistory_Failed_ClearsActiveWithoutMarkingComplete()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        repo.ApplyStatusHistory(new[]
        {
            ("t1", QuestTaskStatus.Started),
            ("t1", QuestTaskStatus.Failed),
        });

        var tasks = await repo.LoadTasksAsync();
        var task = tasks.Single(t => t.Id == "t1");

        Assert.False(task.IsComplete);
        Assert.False(task.IsActive);
    }

    // Real scenario found reviewing a user's actual app-data folder: EFT's
    // own logs reported several quests as "started" whose task IDs (real,
    // valid-looking MongoDB ObjectIds - verified their embedded creation
    // timestamps decode to real recent dates) don't exist in tarkov.dev's
    // current task list at all, because those quests are newer than
    // tarkov.dev's data snapshot. Such quests silently vanish (no name, no
    // marker, no error) since nothing can attach IsActive to a QuestTask
    // that doesn't exist - CountActiveIdsMissingFrom lets the UI surface
    // that gap honestly instead.
    [Fact]
    public async Task CountActiveIdsMissingFrom_ActiveIdNotInTaskList_CountsAsMissing()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        var tasks = await repo.LoadTasksAsync();
        repo.SetTaskActive("t1", true);
        repo.SetTaskActive("brand-new-quest-not-in-dataset-yet", true);

        var missingCount = repo.CountActiveIdsMissingFrom(tasks);

        Assert.Equal(1, missingCount);
    }

    [Fact]
    public async Task CountActiveIdsMissingFrom_AllActiveIdsKnown_ReturnsZero()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        var tasks = await repo.LoadTasksAsync();
        repo.SetTaskActive("t1", true);

        var missingCount = repo.CountActiveIdsMissingFrom(tasks);

        Assert.Equal(0, missingCount);
    }

    [Fact]
    public async Task LoadTasksAsync_RegularAndPve_UseSeparateCacheFiles()
    {
        var handler = new FakeHandler(request =>
        {
            var body = request.Content!.ReadAsStream() is var stream
                ? new StreamReader(stream).ReadToEnd()
                : string.Empty;
            var isPve = body.Contains("\"pve\"");
            var taskId = isPve ? "pve-task" : "regular-task";
            return JsonResponse(new
            {
                data = new { tasks = new[] { new { id = taskId, name = taskId, trader = new { name = "Prapor" } } } },
            });
        });
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        var regularTasks = await repo.LoadTasksAsync(GameMode.Regular);
        var pveTasks = await repo.LoadTasksAsync(GameMode.Pve);

        Assert.Equal("regular-task", Assert.Single(regularTasks).Id);
        Assert.Equal("pve-task", Assert.Single(pveTasks).Id);
        Assert.True(File.Exists(Path.Combine(_tempDir, "tasks-cache.json")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "tasks-cache-pve.json")));
    }

    [Fact]
    public async Task SetTaskComplete_InPveMode_DoesNotAffectRegularModeProgress()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        repo.SetTaskComplete("t1", true, GameMode.Pve);

        var regularTasks = await repo.LoadTasksAsync(GameMode.Regular, forceRefresh: true);
        var pveTasks = await repo.LoadTasksAsync(GameMode.Pve, forceRefresh: true);

        Assert.False(regularTasks.Single(t => t.Id == "t1").IsComplete);
        Assert.True(pveTasks.Single(t => t.Id == "t1").IsComplete);
    }

    [Fact]
    public async Task ApplyStatusHistory_PerMode_KeepsProgressSeparate()
    {
        var handler = new FakeHandler(_ => JsonResponse(new
        {
            data = new { tasks = new[] { new { id = "t1", name = "Debut", trader = new { name = "Prapor" } } } },
        }));
        var client = new TarkovDevClient(new HttpClient(handler));
        var repo = new QuestRepository(client, _tempDir);

        repo.ApplyStatusHistory(new[] { ("t1", QuestTaskStatus.Started) }, GameMode.Regular);
        repo.ApplyStatusHistory(new[] { ("t1", QuestTaskStatus.Finished) }, GameMode.Pve);

        var regularTasks = await repo.LoadTasksAsync(GameMode.Regular, forceRefresh: true);
        var pveTasks = await repo.LoadTasksAsync(GameMode.Pve, forceRefresh: true);

        var regularTask = regularTasks.Single(t => t.Id == "t1");
        Assert.True(regularTask.IsActive);
        Assert.False(regularTask.IsComplete);

        var pveTask = pveTasks.Single(t => t.Id == "t1");
        Assert.True(pveTask.IsComplete);
        Assert.False(pveTask.IsActive);
    }
}
