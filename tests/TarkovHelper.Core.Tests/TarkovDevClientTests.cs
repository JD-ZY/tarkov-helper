using System.Net;
using System.Text;
using TarkovHelper.Core;

namespace TarkovHelper.Core.Tests;

file class FakeHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public FakeHandler(HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}

public class TarkovDevClientTests
{
    [Fact]
    public async Task NormalSuccessResponse_DeserializesTasks()
    {
        const string body = """
            { "data": { "tasks": [ { "id": "t1", "name": "Debut", "trader": { "name": "Prapor" } } ] } }
            """;
        var client = new TarkovDevClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, body)));

        var tasks = await client.GetTasksAsync();

        Assert.Single(tasks);
        Assert.Equal("Debut", tasks[0].Name);
    }

    [Fact]
    public async Task OutageFallbackErrorShape_BareStringArray_ThrowsWithMessage()
    {
        // Verified live: the API returns this exact non-spec-compliant shape
        // (errors as bare strings, not {message: ...} objects) with HTTP 422
        // when the backend worker itself is down.
        const string body = """{"errors":["GraphQL server unavailable. Try again later."]}""";
        var client = new TarkovDevClient(new HttpClient(new FakeHandler(HttpStatusCode.UnprocessableEntity, body)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTasksAsync());

        Assert.Contains("GraphQL server unavailable", ex.Message);
    }

    [Fact]
    public async Task SpecCompliantErrorShape_ObjectWithMessage_ThrowsWithMessage()
    {
        // The GraphQL-over-HTTP spec (graphql-yoga) shape for a normal query
        // execution error, e.g. an invalid field name.
        const string body = """
            { "errors": [ { "message": "Cannot query field \"bogus\" on type \"Task\"." } ] }
            """;
        var client = new TarkovDevClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, body)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTasksAsync());

        Assert.Contains("Cannot query field", ex.Message);
    }

    [Fact]
    public async Task MissingDataField_ReturnsEmptyListRatherThanThrowing()
    {
        const string body = "{}";
        var client = new TarkovDevClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, body)));

        var tasks = await client.GetTasksAsync();

        Assert.Empty(tasks);
    }

    // Real bug: the GraphQL query never requested zones/possibleLocations
    // at all, so every GraphQL-sourced task had empty Zones on every
    // objective regardless of what the real API actually has - the JSON
    // fallback client was the only path that ever populated map markers.
    // This reproduces the real response shape (zones[].map.normalizedName +
    // position.{x,z}, matching TaskZone's schema) and confirms the fix
    // actually populates ObjectiveZone from it.
    [Fact]
    public async Task ObjectiveWithZones_PopulatesObjectiveZoneFromNestedShape()
    {
        const string body = """
            {
              "data": {
                "tasks": [
                  {
                    "id": "t1",
                    "name": "Debut",
                    "trader": { "name": "Prapor" },
                    "objectives": [
                      {
                        "id": "o1",
                        "type": "visit",
                        "description": "Locate the thing",
                        "zones": [
                          { "map": { "normalizedName": "customs" }, "position": { "x": 100.5, "z": -50.25 } }
                        ]
                      }
                    ]
                  }
                ]
              }
            }
            """;
        var client = new TarkovDevClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, body)));

        var tasks = await client.GetTasksAsync();
        var zone = tasks.Single().Objectives.Single().Zones.Single();

        Assert.Equal("customs", zone.MapNormalizedName);
        Assert.Equal(100.5f, zone.X, precision: 1);
        Assert.Equal(-50.25f, zone.Z, precision: 1);
    }

    [Fact]
    public async Task ObjectiveWithPossibleLocations_PopulatesMultipleObjectiveZones()
    {
        const string body = """
            {
              "data": {
                "tasks": [
                  {
                    "id": "t1",
                    "name": "Find the item",
                    "trader": { "name": "Prapor" },
                    "objectives": [
                      {
                        "id": "o1",
                        "type": "findQuestItem",
                        "description": "Find the item",
                        "possibleLocations": [
                          {
                            "map": { "normalizedName": "ground-zero" },
                            "positions": [
                              { "x": 87.7, "z": 225.3 },
                              { "x": 89.1, "z": 224.6 }
                            ]
                          }
                        ]
                      }
                    ]
                  }
                ]
              }
            }
            """;
        var client = new TarkovDevClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, body)));

        var tasks = await client.GetTasksAsync();
        var zones = tasks.Single().Objectives.Single().Zones;

        Assert.Equal(2, zones.Count);
        Assert.All(zones, z => Assert.Equal("ground-zero", z.MapNormalizedName));
    }

    [Fact]
    public async Task ObjectiveWithNoZonesField_ZonesEmptyRatherThanThrowing()
    {
        const string body = """
            {
              "data": {
                "tasks": [
                  {
                    "id": "t1",
                    "name": "Debut",
                    "trader": { "name": "Prapor" },
                    "objectives": [
                      { "id": "o1", "type": "giveItem", "description": "Hand over item" }
                    ]
                  }
                ]
              }
            }
            """;
        var client = new TarkovDevClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, body)));

        var tasks = await client.GetTasksAsync();

        Assert.Empty(tasks.Single().Objectives.Single().Zones);
    }
}
