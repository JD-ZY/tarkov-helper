using System.Net.Http.Json;
using System.Text.Json;

// One-time build tool (not shipped): downloads every item's grid icon image
// from tarkov.dev and writes a manifest, for bundling into the app so icon
// template matching works offline with no first-use download delay. Run
// manually and commit the output to src/TarkovHelper.App/icons/ whenever
// the item catalog needs refreshing (new wipe, new items added).

var outputDir = args.Length > 0 ? args[0] : "icons";
Directory.CreateDirectory(outputDir);
var imagesDir = Path.Combine(outputDir, "images");
Directory.CreateDirectory(imagesDir);

using var http = new HttpClient();

Console.WriteLine("Fetching item catalog...");
var itemsRoot = await http.GetFromJsonAsync<JsonElement>("https://json.tarkov.dev/regular/items");
var namesRoot = await http.GetFromJsonAsync<JsonElement>("https://json.tarkov.dev/regular/items_en");

var names = new Dictionary<string, string>();
if (namesRoot.TryGetProperty("data", out var namesData))
{
    foreach (var prop in namesData.EnumerateObject())
    {
        if (prop.Value.ValueKind == JsonValueKind.String && prop.Name.EndsWith(" Name"))
        {
            names[prop.Name[..^" Name".Length]] = prop.Value.GetString()!;
        }
    }
}

var manifest = new List<IconManifestEntry>();
var items = itemsRoot.GetProperty("data").GetProperty("items");
var itemList = items.EnumerateObject().ToList();
Console.WriteLine($"{itemList.Count} items found. Downloading icons...");

var semaphore = new SemaphoreSlim(8);
var tasks = itemList.Select(async itemProp =>
{
    var id = itemProp.Name;
    var dto = itemProp.Value;

    if (!dto.TryGetProperty("gridImageLink", out var linkEl) || linkEl.ValueKind != JsonValueKind.String)
    {
        return;
    }

    var link = linkEl.GetString();
    if (string.IsNullOrEmpty(link))
    {
        return;
    }

    var width = dto.TryGetProperty("width", out var w) ? w.GetInt32() : 1;
    var height = dto.TryGetProperty("height", out var h) ? h.GetInt32() : 1;
    var name = names.TryGetValue(id, out var n) ? n : id;

    var fileName = $"{id}.webp";
    var filePath = Path.Combine(imagesDir, fileName);

    await semaphore.WaitAsync();
    try
    {
        if (!File.Exists(filePath))
        {
            var bytes = await http.GetByteArrayAsync(link);
            await File.WriteAllBytesAsync(filePath, bytes);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to download {id}: {ex.Message}");
        return;
    }
    finally
    {
        semaphore.Release();
    }

    lock (manifest)
    {
        manifest.Add(new IconManifestEntry(id, name, width, height, fileName));
    }
}).ToList();

await Task.WhenAll(tasks);

Console.WriteLine($"Downloaded {manifest.Count} icons. Writing manifest...");
var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(outputDir, "manifest.json"), manifestJson);

Console.WriteLine("Done.");

record IconManifestEntry(string Id, string Name, int Width, int Height, string FileName);
