using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace TarkovHelper.App;

public readonly record struct ItemHoverResult(string ItemId, string ItemName, float Confidence, bool FromIconMatch);

// Orchestrates the "what item is under my cursor" lookup: tries icon
// template matching first (reliable in dense stash/inventory grids, where
// OCR was confusing neighboring item captions ~60px apart - see
// ItemGridDetector/ItemIconMatcher), and falls back to OCR (which already
// works well for the single floating tooltip shown when hovering loot in a
// raid, where there's no grid to detect at all).
public sealed class ItemHoverLookup : IDisposable
{
    private const int CaptureSize = 500;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, int dwFlags);

    private const int MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    private readonly ItemIconMatcher? _iconMatcher;
    private readonly ItemTooltipReader _tooltipReader;
    private readonly DxgiScreenCapture _screenCapture;
    private readonly string _diagnosticsFolder;

    public ItemHoverLookup(string tessdataPath, string iconsFolder, string diagnosticsFolder)
    {
        _screenCapture = new DxgiScreenCapture();
        _tooltipReader = new ItemTooltipReader(tessdataPath, _screenCapture);
        _diagnosticsFolder = diagnosticsFolder;
        Directory.CreateDirectory(_diagnosticsFolder);

        try
        {
            _iconMatcher = ItemIconMatcher.Load(iconsFolder);
        }
        catch (Exception)
        {
            // Icon matching is an enhancement over pure OCR, not a hard
            // requirement - if the bundled icon library is missing or
            // corrupt, fall back to OCR-only rather than failing outright.
            _iconMatcher = null;
        }
    }

    // Real Win32 per-monitor DPI query, bypassing WinForms' Screen.Bounds
    // entirely - Screen.Bounds is only guaranteed to reflect true physical
    // pixels if this process is explicitly manifested as DPI-aware, which
    // it currently isn't, so its numbers are suspect and unverified. This
    // is the same technique RatScanner itself uses (GetDpiForMonitor) for
    // exactly this reason, per direct research into their real source.
    // Exposed as a diagnostic only for now - not yet wired into the actual
    // cell-size calculation, so this doesn't change behavior, only visibility.
    private static uint GetMonitorDpiAt(System.Drawing.Point screenPoint)
    {
        var monitor = MonitorFromPoint(screenPoint, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) != 0)
        {
            return 96; // 96 = Windows' 100% scaling baseline; used as a "couldn't query" fallback for diagnostics only.
        }

        return dpiX;
    }

    // Set after every Resolve() call (icon-match path only) to a short
    // diagnostic of why icon matching didn't produce a usable result -
    // "no highlighted cell detected" (ItemGridDetector.FindHighlightedCellAt found nothing)
    // vs. "cell WxH has no NxM icons" (a cell WAS traced, but its measured
    // slot size doesn't match any bundled icon's catalogued size, so
    // ItemIconMatcher.Match had zero candidates to compare against). Lets
    // the UI surface which failure mode actually happened for a given
    // item, rather than a single generic "couldn't identify" message -
    // needed to diagnose real-world misses (e.g. a user's M67 grenade)
    // without being able to see their screen.
    public string? LastIconMatchDiagnostic { get; private set; }

    // Returns the best-guess item under the cursor right now. Tries, in
    // order: (1) OCR of EFT's floating hover tooltip, when one is shown;
    // (2) OCR of the item name caption baked directly onto the grid cell,
    // when there's no floating tooltip (confirmed to happen in the
    // character loadout screen, unlike the main stash); (3) icon
    // template-matching as a last resort. All three need a real diagnostic
    // capture to have been found lacking before being added - see the
    // comments on each step below for the specific real failure that
    // justified it.
    //
    // Real bug the OCR-first ordering fixes: icon matching was originally
    // tried first with a near-zero confidence gate (see MinIconConfidence),
    // so it always "won" even when badly wrong - confirmed directly
    // against multiple real captures where pixel-based matching
    // confidently picked a completely unrelated item (e.g. a Morphine
    // syringe matched against a "UHF RFID Reader" card device at 0.89
    // confidence). OCR reading the literal on-screen item name is a far
    // more reliable signal than comparing captured icon pixels against
    // static reference renders, which are vulnerable to EFT's
    // hover-highlight tint, in-game lighting, and rendering differences
    // that pixel-matching couldn't be made reliably robust against
    // despite extensive tuning.
    public ItemHoverResult? Resolve(IReadOnlyDictionary<string, string> itemNames)
    {
        var tooltipLines = _tooltipReader.ReadLinesNearCursor();
        var tooltipMatch = ResolveByOcr(itemNames, tooltipLines);
        if (tooltipMatch is not null)
        {
            return new ItemHoverResult(tooltipMatch.Value.ItemId, tooltipMatch.Value.ItemName, 1f, FromIconMatch: false);
        }

        var (cellRect, screenshotBgr) = FindHoveredCell();
        using var _ = screenshotBgr;

        if (cellRect is not null && screenshotBgr is not null)
        {
            var cursorPosition = Cursor.Position;
            var captureOffsetX = cursorPosition.X - CaptureSize / 2 + cellRect.Value.X;
            var captureOffsetY = cursorPosition.Y - CaptureSize / 2 + cellRect.Value.Y;

            using var cellCropForOcr = new Mat(screenshotBgr, cellRect.Value);
            var captionLines = _tooltipReader.ReadCellCaption(cellCropForOcr, captureOffsetX, captureOffsetY);
            var captionMatch = ResolveByOcr(itemNames, captionLines);
            if (captionMatch is not null)
            {
                return new ItemHoverResult(captionMatch.Value.ItemId, captionMatch.Value.ItemName, 1f, FromIconMatch: false);
            }
        }

        var iconResult = _iconMatcher is not null ? TryIconMatch(cellRect, screenshotBgr) : null;
        if (iconResult is { Confidence: >= MinIconConfidence })
        {
            return new ItemHoverResult(iconResult.Value.ItemId, iconResult.Value.ItemName, iconResult.Value.Confidence, FromIconMatch: true);
        }

        return null;
    }

    // Matches RatScanner's own actual behavior (verified against its real
    // source, RatScanner/RatScannerMain.cs IconScan(): the only gate there
    // is "confidence > 0", i.e. effectively any non-zero match is shown).
    // RatScanner's own maintainer and FAQ openly acknowledge that visually
    // near-identical small items (magazines, keys, attachments) are a
    // known, partly-unfixable weakness of template matching - they don't
    // solve that problem, they just don't hide it behind a stricter
    // threshold or a confidence warning (their ConfWarnThreshold config
    // value exists but is never actually wired to anything in their code).
    // Matching that behavior here rather than being more conservative than
    // the tool this was modeled on.
    private const float MinIconConfidence = 0.01f;

    // Captures once around the cursor and locates the hovered cell's
    // pixel rect within that capture - shared by both the in-cell-caption
    // OCR path and the icon-match fallback, so the capture and detection
    // only happen once per Resolve() call rather than twice. Caller owns
    // the returned Mat's lifetime (dispose it) if non-null.
    private (Rect? CellRect, Mat? ScreenshotBgr) FindHoveredCell()
    {
        var cursorPosition = Cursor.Position;
        var bounds = Screen.FromPoint(cursorPosition).Bounds;
        var scaledSlotSize = ItemGridDetector.ComputeScaledSlotSize(bounds.Width, bounds.Height);
        var monitorDpi = GetMonitorDpiAt(cursorPosition);

        // Captured via DXGI Desktop Duplication (DxgiScreenCapture), not
        // System.Drawing.Graphics.CopyFromScreen - CopyFromScreen is a GDI
        // BitBlt API, confirmed via direct user testing to return stale
        // compositor content over DirectX flip-model surfaces (EFT's own
        // rendering, even in Borderless Windowed mode). See
        // DxgiScreenCapture.cs for the full root-cause writeup.
        var screenshotBgr = _screenCapture.CaptureRegionAroundPoint(cursorPosition, CaptureSize);
        var diagnosticPath = Path.Combine(_diagnosticsFolder, "last-capture.png");

        if (screenshotBgr is null)
        {
            LastIconMatchDiagnostic =
                $"screen capture failed (screen {bounds.Width}x{bounds.Height}, monitor DPI {monitorDpi} = {monitorDpi / 96.0:P0} scale)";
            return (null, null);
        }

        // Diagnostic only, temporary: saves exactly what was captured, so a
        // real failure can be inspected directly instead of guessed at.
        // Overwrites the same file each time rather than accumulating,
        // since only the latest matters.
        try
        {
            Cv2.ImWrite(diagnosticPath, screenshotBgr);
        }
        catch (Exception)
        {
            // Best-effort - a failed diagnostic save must never break the
            // real lookup.
        }

        var pointInCapture = new OpenCvSharp.Point(screenshotBgr.Width / 2, screenshotBgr.Height / 2);

        // Tries lattice-based detection first (computes the cell boundary
        // from the grid's overall line spacing found across the WHOLE
        // capture, rather than tracing a closed rectangle starting at the
        // cursor) - falls back to the older per-cell trace methods only
        // if that fails. Real bug this ordering fixes: the per-cell trace
        // (FindCellAt/FindHighlightedCellAt) requires every edge of the
        // ONE cell under the cursor to be fully intact, which broke
        // across three different real captures in three different ways
        // (a highlight-disrupted border in a stash capture, a highlight
        // color threshold that didn't transfer to a loadout-screen
        // capture, and a highlight-disrupted border spanning too wide a
        // gap for row-retry to route around in a magazine capture).
        // Lattice detection sidesteps all three: it only needs SOME grid
        // lines to be visible anywhere in the capture, not specifically
        // an intact border on the one cell being identified - verified
        // directly against the real magazine capture that broke both
        // older methods.
        var cellRect = ItemGridDetector.FindCellAtByLattice(screenshotBgr, pointInCapture, scaledSlotSize)
            ?? ItemGridDetector.FindCellAt(screenshotBgr, pointInCapture, scaledSlotSize)
            ?? ItemGridDetector.FindHighlightedCellAt(screenshotBgr, pointInCapture, scaledSlotSize);
        if (cellRect is null)
        {
            LastIconMatchDiagnostic =
                $"no cell detected near cursor (screen {bounds.Width}x{bounds.Height}, monitor DPI {monitorDpi} = {monitorDpi / 96.0:P0} scale, " +
                $"assumed cell size {scaledSlotSize}px, capture saved to {diagnosticPath})";
            return (null, screenshotBgr);
        }

        var clampedRect = Rect.FromLTRB(
            Math.Max(0, cellRect.Value.Left),
            Math.Max(0, cellRect.Value.Top),
            Math.Min(screenshotBgr.Width, cellRect.Value.Right),
            Math.Min(screenshotBgr.Height, cellRect.Value.Bottom));

        return (clampedRect, screenshotBgr);
    }

    private (string ItemId, string ItemName, float Confidence)? TryIconMatch(Rect? cellRect, Mat? screenshotBgr)
    {
        if (cellRect is null || screenshotBgr is null)
        {
            return null;
        }

        var bounds = Screen.FromPoint(Cursor.Position).Bounds;
        var scaledSlotSize = ItemGridDetector.ComputeScaledSlotSize(bounds.Width, bounds.Height);
        var diagnosticPath = Path.Combine(_diagnosticsFolder, "last-capture.png");

        using var croppedCell = new Mat(screenshotBgr, cellRect.Value);
        var widthInSlots = Math.Max(1, (int)Math.Round((double)croppedCell.Width / scaledSlotSize));
        var heightInSlots = Math.Max(1, (int)Math.Round((double)croppedCell.Height / scaledSlotSize));

        var iconCount = _iconMatcher!.CountIconsForSize(widthInSlots, heightInSlots);
        var match = _iconMatcher.Match(croppedCell, widthInSlots, heightInSlots);

        LastIconMatchDiagnostic = match is not null
            ? $"cell {croppedCell.Width}x{croppedCell.Height}px -> {widthInSlots}x{heightInSlots} slots, best confidence {match.Value.Confidence:F2}, capture saved to {diagnosticPath}"
            : $"cell {croppedCell.Width}x{croppedCell.Height}px -> {widthInSlots}x{heightInSlots} slots, {iconCount} known icons of that size, capture saved to {diagnosticPath}";

        return match is null ? null : (match.Value.ItemId, match.Value.ItemName, match.Value.Confidence);
    }

    private static (string ItemId, string ItemName)? ResolveByOcr(
        IReadOnlyDictionary<string, string> itemNames, List<OcrLine> candidateLines)
    {
        var cursorPosition = Cursor.Position;
        const float maxDistance = 250f;
        var weightedLines = candidateLines.Select(line =>
        {
            var dx = line.ScreenX - cursorPosition.X;
            var dy = line.ScreenY - cursorPosition.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            return (line.Text, DistanceWeight: Math.Clamp(distance / maxDistance, 0f, 1f));
        });

        return TarkovHelper.Core.ItemLookup.ResolveBestItemMatch(itemNames, weightedLines);
    }

    public void Dispose()
    {
        _tooltipReader.Dispose();
        _iconMatcher?.Dispose();
        _screenCapture.Dispose();
    }
}
