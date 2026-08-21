using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TarkovHelper.Core;

public record UpdateInfo(Version Version, string ZipDownloadUrl, string ReleaseUrl);

// Checks GitHub Releases for a newer build than the one currently running.
// Uses the unauthenticated REST API (60 req/hour per IP is more than enough
// for a once-per-launch check) - the repo is public, so no token is needed.
public class UpdateChecker
{
    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;

    public UpdateChecker(string owner, string repo, HttpClient? httpClient = null)
    {
        _owner = owner;
        _repo = repo;
        _http = httpClient ?? new HttpClient();

        // GitHub's REST API rejects requests with no User-Agent header.
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TarkovHelper", "1.0"));
        }
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    // Returns null if already up to date, no releases exist yet, or the
    // check fails for any reason (network down, rate-limited, etc.) - an
    // update check must never block or crash normal app startup.
    public async Task<UpdateInfo?> CheckForUpdateAsync(Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
            using var response = await _http.GetAsync(url, ct);

            // A repo with no releases yet returns 404 - not an error, just
            // "nothing to update to".
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var root = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.GetString() is not string tagName)
            {
                return null;
            }

            // Release tags are "vX.Y.Z" (see RELEASING.md) - strip the "v"
            // so System.Version can parse it directly.
            var versionText = tagName.StartsWith('v') ? tagName[1..] : tagName;
            if (!Version.TryParse(versionText, out var latestVersion) || latestVersion <= currentVersion)
            {
                return null;
            }

            if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // The release's zip asset is expected to be named
            // "TarkovHelper.zip" (see RELEASING.md) - skip auto-generated
            // "Source code" archives GitHub attaches to every release.
            string? zipUrl = null;
            foreach (var asset in assetsElement.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var nameEl) &&
                    string.Equals(nameEl.GetString(), "TarkovHelper.zip", StringComparison.OrdinalIgnoreCase) &&
                    asset.TryGetProperty("browser_download_url", out var urlEl))
                {
                    zipUrl = urlEl.GetString();
                    break;
                }
            }

            if (zipUrl is null)
            {
                return null;
            }

            var releaseUrl = root.TryGetProperty("html_url", out var htmlUrlEl) ? htmlUrlEl.GetString() ?? url : url;

            return new UpdateInfo(latestVersion, zipUrl, releaseUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<string> DownloadUpdateZipAsync(string zipDownloadUrl, string destinationPath, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(zipDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(destinationPath);
        await httpStream.CopyToAsync(fileStream, ct);

        return destinationPath;
    }
}
