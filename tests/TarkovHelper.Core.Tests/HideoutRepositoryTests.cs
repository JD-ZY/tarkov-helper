using System.Net;
using System.Text;
using System.Text.Json;
using TarkovHelper.Core;
using TarkovHelper.Core.JsonFallback;

namespace TarkovHelper.Core.Tests;

// Fake HttpMessageHandler routes by URL substring so a single handler can
// serve the multiple endpoints GetHideoutStationsAsync fetches
// (hideout, hideout_en, traders, traders_en, items, items_en).
file class FakeHandler : HttpMessageHandler
{
    private readonly Func<string, HttpResponseMessage> _responder;
    public int CallCount { get; private set; }

    public FakeHandler(Func<string, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(_responder(request.RequestUri!.ToString()));
    }
}

public class HideoutRepositoryTests : IDisposable
{
    private readonly string _tempDir;

    public HideoutRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TarkovHelperHideoutTests_" + Guid.NewGuid());
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

    private static JsonTarkovDevClient MakeClientWithOneStation()
    {
        var handler = new FakeHandler(url =>
        {
            if (url.Contains("/hideout_en"))
            {
                return JsonResponse(new { data = new Dictionary<string, string> { ["hideout_area_1_name"] = "Vents" } });
            }
            if (url.Contains("/hideout"))
            {
                return JsonResponse(new
                {
                    data = new Dictionary<string, object>
                    {
                        ["station1"] = new
                        {
                            id = "station1",
                            name = "hideout_area_1_name",
                            normalizedName = "vents",
                            levels = new[] { new { level = 1 } },
                        },
                    },
                });
            }
            // traders/traders_en/items/items_en - empty is fine for this test.
            return JsonResponse(new { data = new Dictionary<string, object>() });
        });

        return new JsonTarkovDevClient(new HttpClient(handler));
    }

    [Fact]
    public async Task FirstLoad_FetchesAndWritesCache()
    {
        var client = MakeClientWithOneStation();
        var repo = new HideoutRepository(client, _tempDir);

        var stations = await repo.LoadStationsAsync();

        Assert.Single(stations);
        Assert.Equal("Vents", stations[0].Name);
        Assert.True(File.Exists(Path.Combine(_tempDir, "hideout-cache.json")));
    }

    [Fact]
    public async Task SecondLoad_UsesCacheWithoutRefetching()
    {
        var handler = new FakeHandler(url =>
        {
            if (url.Contains("/hideout_en"))
            {
                return JsonResponse(new { data = new Dictionary<string, string> { ["hideout_area_1_name"] = "Vents" } });
            }
            if (url.Contains("/hideout"))
            {
                return JsonResponse(new
                {
                    data = new Dictionary<string, object>
                    {
                        ["station1"] = new { id = "station1", name = "hideout_area_1_name", normalizedName = "vents", levels = Array.Empty<object>() },
                    },
                });
            }
            return JsonResponse(new { data = new Dictionary<string, object>() });
        });
        var client = new JsonTarkovDevClient(new HttpClient(handler));
        var repo = new HideoutRepository(client, _tempDir);

        await repo.LoadStationsAsync();
        var callCountAfterFirst = handler.CallCount;
        await repo.LoadStationsAsync();

        Assert.Equal(callCountAfterFirst, handler.CallCount);
    }

    [Fact]
    public async Task SetStationLevel_PersistsAcrossRepositoryInstances()
    {
        var client = MakeClientWithOneStation();

        var repo1 = new HideoutRepository(client, _tempDir);
        await repo1.LoadStationsAsync();
        repo1.SetStationLevel("station1", 2);

        var repo2 = new HideoutRepository(client, _tempDir);
        var stations = await repo2.LoadStationsAsync();

        Assert.Equal(2, stations.Single().CurrentLevel);
    }

    [Fact]
    public async Task DefaultLevel_IsZeroWhenNeverSet()
    {
        var client = MakeClientWithOneStation();
        var repo = new HideoutRepository(client, _tempDir);

        var stations = await repo.LoadStationsAsync();

        Assert.Equal(0, stations.Single().CurrentLevel);
    }

    [Fact]
    public async Task SetStationLevel_ToZero_RemovesEntryRatherThanStoringZero()
    {
        var client = MakeClientWithOneStation();
        var repo = new HideoutRepository(client, _tempDir);
        await repo.LoadStationsAsync();

        repo.SetStationLevel("station1", 3);
        repo.SetStationLevel("station1", 0);

        var repo2 = new HideoutRepository(client, _tempDir);
        var stations = await repo2.LoadStationsAsync();

        Assert.Equal(0, stations.Single().CurrentLevel);
    }
}
