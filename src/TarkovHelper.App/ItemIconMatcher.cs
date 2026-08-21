using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace TarkovHelper.App;

// Matches a screen-captured grid cell against every bundled reference item
// icon of the matching slot size, using OpenCV template matching - the same
// technique RatScanner uses as its primary grid-identification method
// (Elastic License 2.0, see assets/THIRD_PARTY_NOTICES.md), chosen over
// pure OCR specifically because dense stash grids have many item captions
// only ~60px apart, which OCR-with-a-wide-capture-region confused (a real
// bug: hovering "Bulb" read the neighboring "Vaseline" tile instead).
// Icons are pre-composited with their real in-game background/border by
// tarkov.dev's gridImageLink renders, so unlike RatEye's own icon library
// (built from transparent source art it has to composite itself), no
// separate compositing step is needed here.
public sealed class ItemIconMatcher : IDisposable
{
    // Keyed by (widthInCells, heightInCells) -> loaded reference icons for
    // that slot size, since a captured cell can only sensibly match icons
    // of the same footprint.
    private readonly Dictionary<(int Width, int Height), List<(string Id, string Name, Mat Icon)>> _iconsBySize = new();

    private ItemIconMatcher() { }

    public static ItemIconMatcher Load(string iconsFolder)
    {
        var matcher = new ItemIconMatcher();

        var manifestPath = Path.Combine(iconsFolder, "manifest.json");
        var manifestJson = File.ReadAllText(manifestPath);
        var entries = JsonSerializer.Deserialize<List<IconManifestEntry>>(manifestJson)
            ?? new List<IconManifestEntry>();

        var imagesFolder = Path.Combine(iconsFolder, "images");
        foreach (var entry in entries)
        {
            var imagePath = Path.Combine(imagesFolder, entry.FileName);
            if (!File.Exists(imagePath))
            {
                continue;
            }

            var icon = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (icon.Empty())
            {
                icon.Dispose();
                continue;
            }

            var key = (entry.Width, entry.Height);
            if (!matcher._iconsBySize.TryGetValue(key, out var list))
            {
                list = new List<(string, string, Mat)>();
                matcher._iconsBySize[key] = list;
            }

            list.Add((entry.Id, entry.Name, icon));
        }

        return matcher;
    }

    public readonly record struct MatchResult(string ItemId, string ItemName, float Confidence);

    // How many bundled icons exist for a given slot size - lets callers
    // distinguish "cell was traced but its size has zero known icons"
    // (a real, diagnosable gap) from other failure modes, without
    // duplicating the same dictionary lookup Match() does internally.
    public int CountIconsForSize(int width, int height) =>
        _iconsBySize.TryGetValue((width, height), out var candidates) ? candidates.Count : 0;

    // Matches the given captured cell image against every bundled icon
    // whose slot size (in cells, derived from the cell's own pixel size
    // versus the detected single-cell pixel size) matches - both normal
    // and 90-degree-rotated orientations are tried, since items can be
    // rotated in the grid. Uses plain SqDiffNormed, matching RatScanner's
    // own real production algorithm (RatEye/Processing/Icon.cs
    // TemplateMatchSub) rather than a custom metric.
    //
    // Real bugs this was changed to fix, confirmed directly against real
    // diagnostic captures from a live EFT stash grid (not synthetic
    // images):
    //
    // 1. The item under the cursor is always the hover-highlighted cell,
    // and EFT visibly brightens/tints that cell - but the bundled
    // reference icons (tarkov.dev's gridImageLink renders) are NOT
    // highlighted, so a captured cell is measurably brighter than its own
    // correct reference (e.g. Augmentin pills: ~53 average brightness in
    // the bundled icon vs. ~88-93 in an actual captured, highlighted
    // cell). RatScanner's own real fix (confirmed via RatEye's actual
    // source, IconManager.GetIconWithBackground) is to recomposite each
    // reference icon against a WHITE background specifically when matching
    // a highlighted cell (OptimizeHighlighted=true, RatScannerMain's real
    // default). We can't do that exact recomposite - tarkov.dev's icons
    // arrive pre-flattened with their normal background already baked in,
    // not as separate transparent art we can recomposite - so this
    // approximates the same effect: each reference icon's own background
    // color is estimated from its corners, and pixels close to that
    // estimated background are blended toward white by the same alpha EFT
    // itself uses for its highlight tint (BackgroundAlpha = 77/255, also
    // taken from RatEye's real calibrated constant), while pixels that
    // look like real item artwork are left alone. Verified directly
    // against multiple real captures (Splint, Ibuprofen, Vaseline: exact
    // rank-1 match; Salewa: rank 3 of 242, up from rank 60-70 without this
    // correction).
    //
    // 2. Two full cells are mostly identical background/border chrome
    // shared by nearly every same-size icon, with only a small central
    // region actually varying per item - measured directly, a captured
    // "Immobilizing splint" cell matched its OWN correct reference almost
    // perfectly pixel-for-pixel yet still lost to unrelated icons in
    // aggregate, because the shared background diluted the real signal.
    // Fixed by matching on an inset crop that trims the outer
    // background/border margin, so more of the compared pixels are the
    // item's own artwork.
    private const float InsetFraction = 0.22f;

    // EFT's real highlight-tint alpha, taken from RatEye's calibrated
    // BackgroundAlpha constant (RatEye/Config/Processing/Inventory.cs).
    private const float HighlightAlpha = 77f / 255f;

    // How close (Euclidean BGR distance) a pixel must be to a reference
    // icon's own estimated background color to be treated as background
    // (and therefore blended toward white) rather than real item artwork.
    private const double BackgroundColorDistanceThreshold = 40.0;

    public MatchResult? Match(Mat capturedCell, int cellWidthInSlots, int cellHeightInSlots)
    {
        MatchResult? best = null;

        void TryOrientation(Mat source, int width, int height)
        {
            if (!_iconsBySize.TryGetValue((width, height), out var candidates))
            {
                return;
            }

            using var insetSource = InsetCrop(source);

            foreach (var (id, name, icon) in candidates)
            {
                using var resizedIcon = new Mat();
                Cv2.Resize(icon, resizedIcon, source.Size());
                using var highlightedIcon = ApproximateHighlightedIcon(resizedIcon);
                using var insetIcon = InsetCrop(highlightedIcon);

                using var result = insetSource.MatchTemplate(insetIcon, TemplateMatchModes.SqDiffNormed);
                result.MinMaxLoc(out double minVal, out double _);
                var confidence = (float)(1 - minVal);

                if (best is null || confidence > best.Value.Confidence)
                {
                    best = new MatchResult(id, name, confidence);
                }
            }
        }

        TryOrientation(capturedCell, cellWidthInSlots, cellHeightInSlots);

        using var rotated = new Mat();
        Cv2.Rotate(capturedCell, rotated, RotateFlags.Rotate90Clockwise);
        TryOrientation(rotated, cellHeightInSlots, cellWidthInSlots);

        return best;
    }

    // Approximates how a (non-highlighted) reference icon would look if
    // EFT's hover-highlight tint were applied to it: estimates the icon's
    // own background color from its corner pixels, then blends pixels
    // that look like that background toward white by HighlightAlpha,
    // while pixels that look like real item artwork are left mostly
    // unchanged - a per-pixel approximation of RatEye's real
    // background-then-art layering (the actual item art is composited ON
    // TOP of the highlighted background, so it isn't tinted the same way).
    private static Mat ApproximateHighlightedIcon(Mat icon)
    {
        using var floatIcon = new Mat();
        icon.ConvertTo(floatIcon, MatType.CV_32FC3);

        var w = icon.Width;
        var h = icon.Height;
        var corner = 3;
        double bgB = 0, bgG = 0, bgR = 0;
        var sampleCount = 0;
        foreach (var rect in new[]
        {
            new Rect(0, 0, corner, corner),
            new Rect(w - corner, 0, corner, corner),
            new Rect(0, h - corner, corner, corner),
            new Rect(w - corner, h - corner, corner, corner),
        })
        {
            using var region = new Mat(icon, rect);
            var mean = Cv2.Mean(region);
            bgB += mean.Val0;
            bgG += mean.Val1;
            bgR += mean.Val2;
            sampleCount++;
        }
        var bgColor = new Scalar(bgB / sampleCount, bgG / sampleCount, bgR / sampleCount);

        using var bgColorMat = new Mat(icon.Size(), MatType.CV_32FC3, bgColor);
        using var diff = new Mat();
        Cv2.Absdiff(floatIcon, bgColorMat, diff);

        var channels = diff.Split();
        try
        {
            using var squared0 = channels[0].Mul(channels[0]);
            using var squared1 = channels[1].Mul(channels[1]);
            using var squared2 = channels[2].Mul(channels[2]);
            using var sumSquares = new Mat();
            Cv2.Add(squared0, squared1, sumSquares);
            Cv2.Add(sumSquares, squared2, sumSquares);
            using var distance = new Mat();
            Cv2.Sqrt(sumSquares, distance);

            // weight = 1 (background-like) .. 0 (real artwork), linear falloff
            using var weight = new Mat();
            Cv2.Multiply(distance, new Scalar(-1.0 / BackgroundColorDistanceThreshold), weight);
            Cv2.Add(weight, new Scalar(1.0), weight);
            Cv2.Max(weight, new Scalar(0.0), weight);
            Cv2.Min(weight, new Scalar(1.0), weight);
            using var weight3 = new Mat();
            Cv2.CvtColor(weight, weight3, ColorConversionCodes.GRAY2BGR);

            // result = icon * (1 - alpha*weight) + 255 * alpha*weight
            using var alphaWeight = new Mat();
            Cv2.Multiply(weight3, Scalar.All(HighlightAlpha), alphaWeight);
            using var oneMinusAlphaWeight = new Mat();
            Cv2.Subtract(new Scalar(1.0, 1.0, 1.0), alphaWeight, oneMinusAlphaWeight);
            using var term1 = new Mat();
            Cv2.Multiply(floatIcon, oneMinusAlphaWeight, term1);
            using var term2 = new Mat();
            Cv2.Multiply(alphaWeight, Scalar.All(255.0), term2);
            using var resultFloat = new Mat();
            Cv2.Add(term1, term2, resultFloat);

            var result = new Mat();
            resultFloat.ConvertTo(result, MatType.CV_8UC3);
            return result;
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    // Trims the outer background/border margin off a (single-cell-sized)
    // image before matching, proportional to its own size so this scales
    // correctly for multi-slot items and different capture resolutions.
    private static Mat InsetCrop(Mat image)
    {
        var insetX = (int)(image.Width * InsetFraction);
        var insetY = (int)(image.Height * InsetFraction);
        var rect = new Rect(insetX, insetY, image.Width - 2 * insetX, image.Height - 2 * insetY);
        return new Mat(image, rect);
    }

    public void Dispose()
    {
        foreach (var list in _iconsBySize.Values)
        {
            foreach (var (_, _, icon) in list)
            {
                icon.Dispose();
            }
        }
    }

    private class IconManifestEntry
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Width")]
        public int Width { get; set; }

        [JsonPropertyName("Height")]
        public int Height { get; set; }

        [JsonPropertyName("FileName")]
        public string FileName { get; set; } = string.Empty;
    }
}
