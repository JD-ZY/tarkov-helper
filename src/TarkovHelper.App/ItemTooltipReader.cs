using System.Drawing;
using System.IO;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Tesseract;

namespace TarkovHelper.App;

// A single line of OCR'd text plus where on screen it was found - needed so
// callers can prefer text near the cursor over text merely somewhere in the
// (large, necessarily imprecise) capture region. Real bug this fixes: a
// user hovered "Bulb" but a much longer, unrelated item name ("Vaseline
// balm") elsewhere in the capture out-scored it on pure string length, with
// no positional signal to prefer the line actually under the cursor.
public readonly record struct OcrLine(string Text, int ScreenX, int ScreenY);

// Reads the item name out of whatever's near the cursor when the hotkey is
// fired, by screenshotting a region around the cursor and OCR'ing it - not
// real hover detection (no such signal exists - EFT's tooltip is rendered
// client-side with no file/log footprint), but the same external,
// non-memory-reading approach used by TarkovPriceViewer/Tarkov Price
// Overlay for the same problem. Deliberately never touches the EFT process:
// only reads screen pixels, same trust boundary as the screenshot-filename
// position reader already in this app.
public sealed class ItemTooltipReader : IDisposable
{
    // Real-world testing showed the tooltip's position relative to the
    // cursor varies by context (screen edge, grid position, stash vs. raid
    // loot) - a small fixed-offset box consistently missed it and instead
    // captured durability/stack-count numbers next to the item icon. A
    // large box CENTERED on the cursor, covering above/below/left/right,
    // is far more likely to contain the tooltip wherever it renders -
    // ReadLinesNearCursor returns every OCR'd line (with position) rather
    // than assuming position, and the caller fuzzy-matches + distance-
    // weights each one to find the real name.
    private const int CaptureHeight = 500;

    // Wider than tall: real bug this fixes - a long item name ("Monstrum
    // Tactical Compact Prism Scope 2x32") produced a tooltip wider than
    // the original 500px square capture, clipping its right border off
    // the edge of the captured region entirely. Since the border-color
    // detector (FindTooltipBox) needs a CLOSED rectangle to work at all,
    // a clipped border can never be found no matter how the color/shape
    // thresholds are tuned - the fix has to be capturing enough width in
    // the first place. Widened rather than squared up in both dimensions
    // since tooltips grow horizontally with longer names, not vertically.
    private const int CaptureWidth = 800;

    private readonly TesseractEngine _engine;
    private readonly DxgiScreenCapture _screenCapture;

    public ItemTooltipReader(string tessdataPath, DxgiScreenCapture screenCapture)
    {
        _engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default);
        _screenCapture = screenCapture;
    }

    // Real bug this fixes: EFT's tooltip box competes with dozens of other
    // pieces of UI text (item captions, durability numbers, stack counts)
    // in the same 500x500 capture - Tesseract's layout analysis, run
    // across the whole busy capture, either dropped the tooltip's text
    // entirely or fragmented it (e.g. "Morphine injector" came back as
    // just "phine", confirmed directly against a real diagnostic capture).
    // The fix is to find the tooltip's own distinct rectangular region
    // FIRST and OCR only that isolated crop, the same way isolating it
    // manually and re-running OCR produced a clean, exact "Morphine
    // injector" read where the full-image OCR failed.
    //
    // Originally detected via a solid near-black fill color, on the
    // assumption the tooltip would always be visually darker than
    // everything else nearby. Real bug that assumption broke on: a
    // capture with a generally dark surrounding UI (a stash screen with a
    // dark background) had the tooltip's fill blend into the equally-dark
    // background around it, merging into one giant connected region
    // covering almost the whole captured image - the size/shape filter
    // correctly rejected that oversized blob, so no tooltip was found at
    // all despite the tooltip being clearly visible. Fixed by detecting
    // the tooltip's BORDER LINE instead of its fill - EFT draws every
    // panel border (grid lines, tooltips, item cell borders) in the same
    // calibrated blue-gray color (MinGridColorHsv/MaxGridColorHsv, the
    // same constant ItemGridDetector already uses for grid lines),
    // regardless of how dark the fill/background happens to be. That
    // border color reliably outlines the tooltip as a small, closed
    // rectangle even when the fill-darkness signal doesn't stand out -
    // verified directly against the real capture that broke the old
    // dark-fill detector.
    private const int MinTooltipWidthPx = 60;
    private const int MaxTooltipHeightPx = 60;
    private const double MinTooltipAspectRatio = 2.0;

    // Calibrated from real captures: a genuine tooltip's average interior
    // brightness measured ~11 (near-black background dominates even with
    // bright text); the real false-positive grid cell measured ~24-44
    // (icon art is generally much brighter). Set roughly midway, biased
    // toward the tooltip side since a solid dark background is the more
    // load-bearing assumption to protect.
    private const double MaxTooltipInteriorBrightness = 20.0;

    private static readonly Scalar MinBorderColorHsv = new(100, 15, 63);
    private static readonly Scalar MaxBorderColorHsv = new(146, 46, 96);

    // Returns every non-blank OCR'd line found in the tooltip box nearest
    // the current cursor position, each with its on-screen center point.
    // Caller is responsible for matching each line against real item names
    // (see ItemLookup.ResolveBestItemMatch) and picking the best result,
    // weighting both text-match quality and proximity to the cursor - OCR
    // output alone doesn't say which line (if any) is the item name versus
    // other text (e.g. a multi-line description) also in the tooltip.
    public List<OcrLine> ReadLinesNearCursor()
    {
        var cursorPosition = Cursor.Position;
        var captureX = cursorPosition.X - CaptureWidth / 2;
        var captureY = cursorPosition.Y - CaptureHeight / 2;

        // Captured via DXGI Desktop Duplication, not
        // System.Drawing.Graphics.CopyFromScreen - CopyFromScreen is a GDI
        // BitBlt API, confirmed via direct user testing to return stale
        // compositor content over DirectX flip-model surfaces (EFT's own
        // rendering). See DxgiScreenCapture.cs for the full root-cause
        // writeup; this reader had the same exposure as the icon-match
        // capture path already fixed there.
        using var capturedMat = _screenCapture.CaptureRegionAroundPoint(cursorPosition, CaptureWidth, CaptureHeight);
        if (capturedMat is null)
        {
            return new List<OcrLine>();
        }

        var tooltipRect = FindTooltipBox(capturedMat, new OpenCvSharp.Point(capturedMat.Width / 2, capturedMat.Height / 2));
        if (tooltipRect is null)
        {
            return new List<OcrLine>();
        }

        using var tooltipCrop = new Mat(capturedMat, tooltipRect.Value);
        using var gray = tooltipCrop.CvtColor(ColorConversionCodes.BGR2GRAY);

        const int upscaleFactor = 4;
        using var upscaled = new Mat();
        Cv2.Resize(gray, upscaled, new OpenCvSharp.Size(gray.Width * upscaleFactor, gray.Height * upscaleFactor), 0, 0, InterpolationFlags.Cubic);

        using var thresholded = new Mat();
        Cv2.Threshold(upscaled, thresholded, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        using var bitmap = thresholded.ToBitmap();
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(memoryStream.ToArray());
        using var page = _engine.Process(pix, PageSegMode.SparseText);

        var results = new List<OcrLine>();
        using var iterator = page.GetIterator();
        iterator.Begin();

        do
        {
            var text = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length < 3)
            {
                continue;
            }

            if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds))
            {
                continue;
            }

            // Bounds are in upscaled, tooltip-crop-relative pixel space -
            // map back through the upscale factor, then through the
            // tooltip crop's own offset, then to absolute screen coordinates.
            var centerXInCrop = (bounds.X1 + bounds.X2) / 2 / upscaleFactor;
            var centerYInCrop = (bounds.Y1 + bounds.Y2) / 2 / upscaleFactor;
            var screenX = captureX + tooltipRect.Value.X + centerXInCrop;
            var screenY = captureY + tooltipRect.Value.Y + centerYInCrop;

            results.Add(new OcrLine(text, screenX, screenY));
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return results;
    }

    // Reads the item name caption baked directly onto a stash/inventory
    // grid cell (e.g. "Bolts", "Salewa") - distinct from the floating
    // tooltip ReadLinesNearCursor reads, and needed for contexts where no
    // floating tooltip appears at all. Real bug this fixes: hovering an
    // item in the character loadout screen (as opposed to the main stash)
    // showed only an in-cell caption, no floating tooltip - confirmed
    // directly against a real "Bolts" capture, where ReadLinesNearCursor's
    // tooltip-box detector correctly found nothing (there was no floating
    // tooltip to find), silently leaving OCR with zero candidates even
    // though the cell's own caption text was sitting right there.
    // Callers already have the cell rect from grid detection (needed for
    // the icon-match fallback anyway), so this takes it directly rather
    // than re-running detection.
    public List<OcrLine> ReadCellCaption(Mat cellCropBgr, int captureOffsetX, int captureOffsetY)
    {
        using var gray = cellCropBgr.CvtColor(ColorConversionCodes.BGR2GRAY);

        const int upscaleFactor = 4;
        using var upscaled = new Mat();
        Cv2.Resize(gray, upscaled, new OpenCvSharp.Size(gray.Width * upscaleFactor, gray.Height * upscaleFactor), 0, 0, InterpolationFlags.Cubic);

        // Adaptive thresholding, not a single global Otsu threshold - a
        // grid cell mixes bright icon art, dark background, and the
        // caption text itself at varying local brightness, unlike the
        // floating tooltip's uniform near-black background. Verified
        // directly: adaptive thresholding cleanly separated a real
        // "Bolts" cell's caption text from its icon art, where a global
        // threshold left it noisy/unreadable.
        using var thresholded = new Mat();
        Cv2.AdaptiveThreshold(upscaled, thresholded, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 31, -10);

        using var bitmap = thresholded.ToBitmap();
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(memoryStream.ToArray());
        using var page = _engine.Process(pix, PageSegMode.SparseText);

        var results = new List<OcrLine>();
        using var iterator = page.GetIterator();
        iterator.Begin();

        do
        {
            var text = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length < 3)
            {
                continue;
            }

            if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds))
            {
                continue;
            }

            var centerXInCrop = (bounds.X1 + bounds.X2) / 2 / upscaleFactor;
            var centerYInCrop = (bounds.Y1 + bounds.Y2) / 2 / upscaleFactor;
            var screenX = captureOffsetX + centerXInCrop;
            var screenY = captureOffsetY + centerYInCrop;

            results.Add(new OcrLine(text, screenX, screenY));
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return results;
    }

    // Finds the bounding box of EFT's tooltip - identified by its border
    // line color (see MinBorderColorHsv/MaxBorderColorHsv), a rectangle
    // wider than it is tall - nearest the given point. Contours are ranked
    // by distance to the point rather than requiring the point to be
    // strictly inside one, since the tooltip is usually offset from the
    // cursor (rendered above/below/beside it, not under it).
    private static OpenCvSharp.Rect? FindTooltipBox(Mat capturedBgr, OpenCvSharp.Point nearPoint)
    {
        using var hsv = capturedBgr.CvtColor(ColorConversionCodes.BGR2HSV_FULL);
        using var borderMask = hsv.InRange(MinBorderColorHsv, MaxBorderColorHsv);

        // Dilate to bridge small gaps in the border line (anti-aliasing,
        // compression noise) so the outline forms one closed shape -
        // RETR_LIST (not RETR_EXTERNAL) is required here since the
        // border is a hollow outline, not a filled blob: EXTERNAL mode
        // would only trace the outermost contour of whatever larger
        // shape this outline happens to be touching/overlapping, the
        // same "everything merges into one big contour" failure mode
        // that broke the old dark-fill detection.
        using var dilated = new Mat();
        Cv2.Dilate(borderMask, dilated, Mat.Ones(3, 3));

        var contours = Cv2.FindContoursAsArray(dilated, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        OpenCvSharp.Rect? best = null;
        var bestDistanceSq = double.MaxValue;

        foreach (var contour in contours)
        {
            var box = Cv2.BoundingRect(contour);
            if (box.Width < MinTooltipWidthPx || box.Height > MaxTooltipHeightPx)
            {
                continue;
            }

            if (box.Width / (double)Math.Max(1, box.Height) < MinTooltipAspectRatio)
            {
                continue;
            }

            // Real bug this filter fixes: a wide, short grid cell (e.g. a
            // suppressor's long 1x3 icon) can also be border-colored,
            // wide, and short enough to pass the checks above, and won as
            // a false positive when it happened to sit closer to the
            // cursor than the real (but momentarily undetected) tooltip -
            // confirmed directly against a real capture where "M4SD-K"
            // (a grid cell caption) was returned instead of the actual
            // hovered item's tooltip. Tried rejecting by interior
            // variance first (assuming a tooltip's flat background would
            // be more uniform than icon artwork) - measured directly
            // against both a real tooltip and the real false-positive
            // cell, and it doesn't hold: the tooltip's white text on
            // black actually produced HIGHER variance (~32) than the
            // grid cell's muted icon art (~24). What DOES hold up: mean
            // brightness - the tooltip's near-black background dominates
            // its average (~11) even with bright text, while a grid
            // cell's icon art is generally much brighter overall (~32-44,
            // measured on the real false positive). Rejecting bright
            // interiors keeps grid cells from qualifying as a tooltip
            // candidate at all, rather than relying on a distance
            // tiebreak to prefer the real tooltip over them.
            using var interior = new Mat(capturedBgr, box);
            var interiorMean = Cv2.Mean(interior);
            var interiorBrightness = (interiorMean.Val0 + interiorMean.Val1 + interiorMean.Val2) / 3.0;
            if (interiorBrightness > MaxTooltipInteriorBrightness)
            {
                continue;
            }

            var centerX = box.X + box.Width / 2.0;
            var centerY = box.Y + box.Height / 2.0;
            var dx = centerX - nearPoint.X;
            var dy = centerY - nearPoint.Y;
            var distanceSq = dx * dx + dy * dy;

            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                best = box;
            }
        }

        return best;
    }

    public void Dispose() => _engine.Dispose();
}
