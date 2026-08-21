using System.Net;
using System.Text;
using System.Text.Json;
using TarkovHelper.Core;

namespace TarkovHelper.Core.Tests;

file class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_responder(request));
}

public class UpdateCheckerTests
{
    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static object ReleasePayload(string tagName, params (string Name, string Url)[] assets) => new
    {
        tag_name = tagName,
        html_url = $"https://github.com/JD-ZY/tarkov-helper/releases/tag/{tagName}",
        assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url }).ToArray(),
    };

    [Fact]
    public async Task NewerVersionAvailable_ReturnsUpdateInfo()
    {
        var handler = new FakeHandler(_ => JsonResponse(
            ReleasePayload("v1.2.0", ("TarkovHelper.zip", "https://example.com/TarkovHelper.zip"))));
        var checker = new UpdateChecker("JD-ZY", "tarkov-helper", new HttpClient(handler));

        var result = await checker.CheckForUpdateAsync(new Version(1, 0, 0));

        Assert.NotNull(result);
        Assert.Equal(new Version(1, 2, 0), result!.Version);
        Assert.Equal("https://example.com/TarkovHelper.zip", result.ZipDownloadUrl);
    }

    [Fact]
    public async Task SameVersion_ReturnsNull()
    {
        var handler = new FakeHandler(_ => JsonResponse(
            ReleasePayload("v1.0.0", ("TarkovHelper.zip", "https://example.com/TarkovHelper.zip"))));
        var checker = new UpdateChecker("JD-ZY", "tarkov-helper", new HttpClient(handler));

        var result = await checker.CheckForUpdateAsync(new Version(1, 0, 0));

        Assert.Null(result);
    }

    [Fact]
    public async Task OlderReleaseVersion_ReturnsNull()
    {
        var handler = new FakeHandler(_ => JsonResponse(
            ReleasePayload("v0.9.0", ("TarkovHelper.zip", "https://example.com/TarkovHelper.zip"))));
        var checker = new UpdateChecker("JD-ZY", "tarkov-helper", new HttpClient(handler));

        var result = await checker.CheckForUpdateAsync(new Version(1, 0, 0));

        Assert.Null(result);
    }

    [Fact]
    public async Task NoReleasesYet_404_ReturnsNullRatherThanThrowing()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = new UpdateChecker("JD-ZY", "tarkov-helper", new HttpClient(handler));

        var result = await checker.CheckForUpdateAsync(new Version(1, 0, 0));

        Assert.Null(result);
    }

    [Fact]
    public async Task NetworkFailure_ReturnsNullRatherThanThrowing()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("network down"));
        var checker = new UpdateChecker("JD-ZY", "tarkov-helper", new HttpClient(handler));

        var result = await checker.CheckForUpdateAsync(new Version(1, 0, 0));

        Assert.Null(result);
    }

    [Fact]
    public async Task NewerVersionButNoZipAsset_ReturnsNull()
    {
        // A release with only the auto-generated "Source code" archives and
        // no TarkovHelper.zip (e.g. a draft/manual release missing its
        // upload) must not be offered as an update - there'd be nothing to
        // actually download.
        var handler = new FakeHandler(_ => JsonResponse(
            ReleasePayload("v2.0.0", ("Source code (zip)", "https://example.com/source.zip"))));
        var checker = new UpdateChecker("JD-ZY", "tarkov-helper", new HttpClient(handler));

        var result = await checker.CheckForUpdateAsync(new Version(1, 0, 0));

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadUpdateZipAsync_WritesResponseBodyToDestinationPath()
    {
        var content = "fake zip bytes"u8.ToArray();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        });
        var checker = new UpdateChecker("JD-ZY", "tarkov-helper", new HttpClient(handler));
        var destination = Path.Combine(Path.GetTempPath(), "UpdateCheckerTests_" + Guid.NewGuid() + ".zip");

        try
        {
            await checker.DownloadUpdateZipAsync("https://example.com/TarkovHelper.zip", destination);

            Assert.True(File.Exists(destination));
            Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }
}
