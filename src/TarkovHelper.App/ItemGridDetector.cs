using System.Linq;
using OpenCvSharp;

namespace TarkovHelper.App;

// Detects the exact pixel boundaries of EFT inventory grid cells in a
// screenshot, so an OCR/icon-matching lookup can crop precisely to the one
// cell under the cursor instead of a wide, ambiguous radius. Ported from
// RatEye's Processing/Inventory.cs (https://github.com/RatScanner/RatEye,
// Elastic License 2.0) - see assets/THIRD_PARTY_NOTICES.md. Real bug this
// exists to fix: a wide OCR capture region picked up a neighboring grid
// cell's caption instead of the one actually under the cursor, since
// adjacent cells are only ~60px apart and nothing constrained the capture
// to a single cell's exact boundary.
public static class ItemGridDetector
{
    // HSV thresholds for detecting grid line pixels - verified against
    // RatEye's calibrated constant (RatEye/Config/Processing/Inventory.cs).
    private static readonly Scalar MinGridColorHsv = new(100, 15, 63);
    private static readonly Scalar MaxGridColorHsv = new(146, 46, 96);

    // Cell size in pixels at 1920x1080 - same source (RatEye's
    // BaseSlotSize). Scales linearly with the smaller of the screen's
    // width/height ratio to 1920x1080, matching how EFT's UI actually
    // scales (confirmed: no separate interface-scale axis exists in EFT,
    // resolution is the only scaling input).
    private const float BaseSlotSize = 63f;

    // Set to a file path prefix to dump intermediate detection masks as
    // PNGs for debugging - null (default) in normal operation.
    public static string? DebugDumpPath;

    public static float ComputeScale(int screenWidth, int screenHeight) =>
        Math.Min(screenWidth / 1920f, screenHeight / 1080f);

    public static int ComputeScaledSlotSize(int screenWidth, int screenHeight) =>
        (int)(BaseSlotSize * ComputeScale(screenWidth, screenHeight));

    // Finds the pixel rectangle of whichever detected grid cell contains
    // the given point (screen coordinates, relative to the captured
    // region's own top-left origin), or null if no cell boundary could be
    // traced there (e.g. the cursor isn't actually over a grid at all).
    public static Rect? FindCellAt(Mat screenshotBgr, OpenCvSharp.Point point, int scaledSlotSize)
    {
        using var hsv = screenshotBgr.CvtColor(ColorConversionCodes.BGR2HSV_FULL);
        using var colorFilter = hsv.InRange(MinGridColorHsv, MaxGridColorHsv);

        var lineSize = scaledSlotSize - (scaledSlotSize % 2) + 1;
        using var lineStructure = Mat.Ones(MatType.CV_8U, new[] { lineSize, 1 });
        using var verticalLines = colorFilter.Erode(lineStructure);
        Cv2.Dilate(verticalLines, verticalLines, lineStructure);

        using var lineStructureT = lineStructure.T();
        using var horizontalLines = colorFilter.Erode(lineStructureT);
        Cv2.Dilate(horizontalLines, horizontalLines, lineStructureT);

        SmoothJaggedLines(verticalLines, false, scaledSlotSize);
        SmoothJaggedLines(horizontalLines, true, scaledSlotSize);

        using var grid = CombineWithoutGaps(verticalLines, horizontalLines, scaledSlotSize);
        Cv2.Dilate(grid, grid, Mat.Ones(2, 2));
        Cv2.Erode(grid, grid, Mat.Ones(2, 2));

        if (DebugDumpPath is not null)
        {
            Cv2.ImWrite(DebugDumpPath + "_colorfilter.png", colorFilter);
            Cv2.ImWrite(DebugDumpPath + "_vertlines.png", verticalLines);
            Cv2.ImWrite(DebugDumpPath + "_horizlines.png", horizontalLines);
            Cv2.ImWrite(DebugDumpPath + "_grid.png", grid);
        }

        return TraceCellContaining(grid, point, scaledSlotSize);
    }

    // Finds the cursor's cell boundary via the grid's overall lattice
    // spacing, rather than by tracing a closed rectangle starting from the
    // cursor's own position. Real bug this fixes: TraceCellContaining
    // (used by FindCellAt/FindHighlightedCellAt) walks all 4 edges of the
    // ONE cell containing the cursor, so it fails outright whenever ANY
    // edge of that specific cell has a gap - confirmed directly against a
    // real "GEN M3" magazine capture where the highlight tint broke the
    // cell's right-side border across roughly half its height (not a
    // small, few-pixel gap the existing row-retry logic could route
    // around - every possible starting row still had to complete a walk
    // through that same broken edge). This method sidesteps that
    // entirely: it sums grid-line pixels across EVERY row/column of the
    // WHOLE capture (not just near the cursor), so a gap local to the
    // highlighted cell barely dents the aggregate signal, then finds the
    // lattice line position immediately before and after the cursor on
    // each axis and uses simple arithmetic to compute the cell boundary -
    // no cell-specific border needs to be intact anywhere. Verified
    // directly against the real GEN M3 capture (which broke both
    // FindCellAt and FindHighlightedCellAt): correctly computes a clean
    // 63x65px cell matching the expected 1-slot size.
    public static Rect? FindCellAtByLattice(Mat screenshotBgr, OpenCvSharp.Point point, int scaledSlotSize)
    {
        using var hsv = screenshotBgr.CvtColor(ColorConversionCodes.BGR2HSV_FULL);
        using var colorFilter = hsv.InRange(MinGridColorHsv, MaxGridColorHsv);

        if (DebugDumpPath is not null)
        {
            Cv2.ImWrite(DebugDumpPath + "_latticefilter.png", colorFilter);
        }

        var columnLines = FindLatticeLines(colorFilter, horizontal: false, scaledSlotSize);
        var rowLines = FindLatticeLines(colorFilter, horizontal: true, scaledSlotSize);

        var left = BracketBelow(columnLines, point.X);
        var right = BracketAbove(columnLines, point.X);
        var top = BracketBelow(rowLines, point.Y);
        var bottom = BracketAbove(rowLines, point.Y);

        if (left is null || right is null || top is null || bottom is null)
        {
            return null;
        }

        var width = right.Value - left.Value;
        var height = bottom.Value - top.Value;
        if (width < scaledSlotSize / 2 || height < scaledSlotSize / 2)
        {
            return null;
        }

        return new Rect(left.Value, top.Value, width, height);
    }

    // Sums grid-line-colored pixels across every row (for column
    // positions) or column (for row positions) of the whole mask, then
    // returns the center of each contiguous run whose peak sum clears
    // BOTH a minimum-coverage floor and a relative-strength bar (a
    // fraction of the single strongest candidate found) - a real grid
    // line spans most of the capture's height/width, so it produces a
    // strong, wide peak even if a short section of it (e.g. behind the
    // highlighted cell) is broken.
    //
    // Real bug the relative-strength bar fixes: some item icons (e.g. a
    // magazine's art, which has a visually distinct boundary between its
    // body and its ammo-count bar) contain their own internal horizontal
    // line that also passes the grid-line color filter and spans enough
    // of the capture's width to look like a real lattice line on its own
    // - confirmed directly against a real "GEN M3" magazine capture,
    // where this false internal line split one genuine 1x2 cell into what
    // looked like two separate 1x1 cells. Measured directly: the real
    // lattice lines bordering that cell had pixel-sums of 195 and
    // 197-325 (out of a 500px capture), while the false internal line
    // measured only 139 - a real border line is doubled (each of the two
    // adjacent cells draws its own edge, 5-6px apart) and so consistently
    // registers far stronger than a single internal icon-art line, even
    // though both may clear the same low absolute floor.
    private static List<int> FindLatticeLines(Mat colorFilterMask, bool horizontal, int scaledSlotSize)
    {
        var length = horizontal ? colorFilterMask.Rows : colorFilterMask.Cols;
        var sums = new int[length];

        using var reduced = new Mat();
        Cv2.Reduce(colorFilterMask, reduced, horizontal ? ReduceDimension.Column : ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32SC1.Value);
        for (var i = 0; i < length; i++)
        {
            sums[i] = (horizontal ? reduced.At<int>(i, 0) : reduced.At<int>(0, i)) / 255;
        }

        // A real grid line, even partly disrupted, still lights up a
        // clear majority of the capture's other dimension - a low bar
        // (a small fraction of the capture's own size) comfortably
        // separates real-line candidates from background noise, before
        // the relative-strength filter below further separates real
        // (doubled) lattice lines from single internal icon-art lines.
        var otherDimension = horizontal ? colorFilterMask.Cols : colorFilterMask.Rows;
        var minCoverage = Math.Max(10, otherDimension / 12);

        var candidates = new List<(int Position, int Peak)>();
        var runStart = -1;
        var runPeak = 0;
        for (var i = 0; i < length; i++)
        {
            var isLine = sums[i] >= minCoverage;
            if (isLine)
            {
                if (runStart < 0)
                {
                    runStart = i;
                    runPeak = sums[i];
                }
                else
                {
                    runPeak = Math.Max(runPeak, sums[i]);
                }
            }
            else if (runStart >= 0)
            {
                candidates.Add(((runStart + i - 1) / 2, runPeak));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            candidates.Add(((runStart + length - 1) / 2, runPeak));
        }

        if (candidates.Count == 0)
        {
            return new List<int>();
        }

        var strongestPeak = candidates.Max(c => c.Peak);
        const double MinRelativeStrength = 0.6;
        return candidates
            .Where(c => c.Peak >= strongestPeak * MinRelativeStrength)
            .Select(c => c.Position)
            .ToList();
    }

    private static int? BracketBelow(List<int> sortedPositions, int point)
    {
        int? best = null;
        foreach (var position in sortedPositions)
        {
            if (position <= point && (best is null || position > best.Value))
            {
                best = position;
            }
        }

        return best;
    }

    private static int? BracketAbove(List<int> sortedPositions, int point)
    {
        int? best = null;
        foreach (var position in sortedPositions)
        {
            if (position >= point && (best is null || position < best.Value))
            {
                best = position;
            }
        }

        return best;
    }

    private static void SmoothJaggedLines(Mat mat, bool horizontal, int scaledSlotSize)
    {
        var size = scaledSlotSize * 2;
        size -= size % 2 - 1;
        var extendSize = horizontal ? new[] { 1, size } : new[] { size, 1 };
        using var extendStructure = Mat.Ones(MatType.CV_8U, extendSize).ToMat();

        var thickenSize = horizontal ? new[] { 3, 1 } : new[] { 1, 3 };
        using var thickenStructure = Mat.Ones(MatType.CV_8U, thickenSize).ToMat();

        using var extended = new Mat();
        using var thickened = new Mat();
        Cv2.Dilate(mat, extended, extendStructure, null, 10);
        Cv2.Dilate(mat, thickened, thickenStructure);
        Cv2.BitwiseAnd(extended, thickened, mat);
    }

    private static Mat CombineWithoutGaps(Mat verticalLines, Mat horizontalLines, int scaledSlotSize)
    {
        using var holes = new Mat();
        Cv2.BitwiseAnd(verticalLines, horizontalLines, holes);
        using var vWithHoles = new Mat();
        Cv2.BitwiseXor(verticalLines, holes, vWithHoles);
        using var hWithHoles = new Mat();
        Cv2.BitwiseXor(horizontalLines, holes, hWithHoles);

        var halfSlot = scaledSlotSize / 2;
        var size = halfSlot - (halfSlot % 2) + 1;

        using var vStructure = Mat.Ones(MatType.CV_8U, new[] { size, 1 }).ToMat();
        Cv2.Erode(vWithHoles, vWithHoles, vStructure);
        Cv2.Dilate(vWithHoles, vWithHoles, vStructure);

        using var hStructure = Mat.Ones(MatType.CV_8U, new[] { 1, size }).ToMat();
        Cv2.Erode(hWithHoles, hWithHoles, hStructure);
        Cv2.Dilate(hWithHoles, hWithHoles, hStructure);

        var grid = new Mat();
        Cv2.BitwiseOr(vWithHoles, hWithHoles, grid);
        Cv2.BitwiseOr(grid, holes, grid);
        return grid;
    }

    // Real bug: the boundary walk originally searched along a single exact
    // row (the cursor's own Y) to find the cell's left edge. If that one
    // row's grid-line pixel wasn't cleanly detected - e.g. the cursor
    // happened to sit over part of a round/irregular item icon (a grenade)
    // rather than clear background, or ordinary screen-capture/compression
    // noise - the whole trace failed outright with zero fallback, even
    // though the real cell boundary was perfectly traceable a few pixels
    // above or below. Trying a small band of nearby rows before giving up
    // makes this robust to that single-row noise without weakening the
    // trace itself (each attempt is still an exact single-row search, just
    // retried at different starting rows).
    // Widened from an original {0,-3,3,-6,6,-10,10} after a real capture
    // (a "GEN M3" magazine, hover-highlighted) showed the highlight tint
    // breaking the grid-line border across a gap roughly 25px wide -
    // wider than the original +/-10px search band covered. Highlight-tint
    // color varies by capture in a way that's proven hard to threshold
    // reliably (see FindHighlightedCellAt's own comments - three different
    // real captures needed three different color calibrations), but the
    // GEOMETRIC size of the gap it leaves in the grid line is a more
    // stable thing to search around, so this widens the search band
    // itself rather than trying to get the highlight color exactly right.
    private static readonly int[] RowOffsetsToTry =
    {
        0, -3, 3, -6, 6, -10, 10, -15, 15, -20, 20, -25, 25, -30, 30, -35, 35,
    };

    // Walks along the innermost edge of the grid cell containing the given
    // point to find its four corners - adapted from RatEye's TryAddIcon
    // (which scans the whole image calling this logic at every candidate
    // position; this only needs the one cell under the cursor, so it seeds
    // the walk from the point instead of a grid scan, and - since the
    // starting point here is an arbitrary point inside the cell rather than
    // a point already known to be on the cell's top edge - skips the
    // origin-closing verification RatEye's version does, which only makes
    // sense when the start point IS the top edge). Indexer convention
    // matches the source: grid.At<byte>(row, col).
    private static Rect? TraceCellContaining(Mat grid, OpenCvSharp.Point point, int scaledSlotSize)
    {
        foreach (var rowOffset in RowOffsetsToTry)
        {
            var result = TraceCellContainingAtRow(grid, point, scaledSlotSize, rowOffset);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static Rect? TraceCellContainingAtRow(Mat grid, OpenCvSharp.Point point, int scaledSlotSize, int rowOffset)
    {
        var rows = grid.Rows;
        var cols = grid.Cols;

        // Search left from the point for the nearest vertical grid line -
        // the cursor itself is inside a cell, not on a line, so this finds
        // the cell's left edge to start the walk from.
        var startX = point.X;
        var startY = Math.Clamp(point.Y + rowOffset, 0, rows - 1);
        while (startX > 0 && grid.At<byte>(startY, startX) != 0xFF)
        {
            startX--;
        }

        if (startX <= 0)
        {
            return null;
        }

        var x = startX;
        var y = startY;
        int a, b, c, d;

        // Go south to the bottom-left corner.
        for (a = y; a < rows; a++)
        {
            if (grid.At<byte>(a, x) == 0x00)
            {
                return null;
            }

            if (grid.At<byte>(a, x + 1) == 0xFF)
            {
                break;
            }

            if (!(a + 1 < rows))
            {
                return null;
            }
        }

        // Go east to the bottom-right corner.
        for (b = x + 1; b < cols; b++)
        {
            if (grid.At<byte>(a, b) == 0x00)
            {
                return null;
            }

            if (grid.At<byte>(a - 1, b) == 0xFF)
            {
                break;
            }

            if (!(b + 1 < cols))
            {
                return null;
            }
        }

        // Go north to the top-right corner.
        for (c = a - 1; c > 0; c--)
        {
            if (grid.At<byte>(c, b) == 0x00)
            {
                return null;
            }

            if (grid.At<byte>(c, b - 1) == 0xFF)
            {
                break;
            }

            if (!(c - 1 > 0))
            {
                return null;
            }
        }

        // Go west to the top-left corner.
        for (d = b - 1; d >= x; d--)
        {
            if (grid.At<byte>(c, d) == 0x00)
            {
                return null;
            }

            if (grid.At<byte>(c + 1, d) == 0xFF)
            {
                break;
            }

            if (!(d - 1 >= x))
            {
                return null;
            }
        }

        var width = b - d + 1;
        var height = a - c + 1;

        // Sanity check: the traced rectangle should be a plausible
        // single-or-multi-cell size, not some degenerate sliver.
        if (width < scaledSlotSize / 2 || height < scaledSlotSize / 2)
        {
            return null;
        }

        return new Rect(d, c, width, height);
    }

    // Detects the highlighted (hovered) cell directly, instead of tracing
    // grid lines - ported from RatEye's DetectInventoryGridHighlighted /
    // ParseInventoryGridHighlighted / LocateIconHighlighted
    // (RatEye/Processing/Inventory.cs), which is RatScanner's own real
    // production configuration for this exact scenario (hovering an item):
    // confirmed via RatScannerMain.GetRatEyeConfig(), called with
    // OptimizeHighlighted defaulted to true. Real bug this exists to fix:
    // the item currently under the cursor is ALWAYS the hovered/highlighted
    // one, and EFT draws a highlight tint + border over that exact cell -
    // which was breaking the normal grid-line-walk (FindCellAt) at exactly
    // the one cell that lookup always needs, since the highlight pixels
    // don't match the plain grid-line color it looks for (verified via a
    // real diagnostic capture: the grid-line color filter had a visible gap
    // at the highlighted cell's border, causing the trace to leak into a
    // neighboring row). This mirrors the real content instead: threshold
    // for the highlight color itself, then take the bounding box of the
    // (dilated, contour-merged) highlighted region.
    //
    // Note: RatEye's own MinHighlightingColor/MaxHighlightingColor
    // constants ((0,0,80)-(255,3,100), near-white/gray) did NOT match this
    // app's real captures - measured directly from a diagnostic screenshot,
    // this client's highlight color is HSV_FULL (~120-160, ~5-20, ~85-115),
    // a pale blue-gray tint, not near-white. The threshold below uses the
    // measured value, not RatEye's stale constant - the DETECTION STRATEGY
    // (threshold + contour bbox) is the real port; the specific color had
    // to be recalibrated against this app's own real captures, same as
    // RatEye's own MinGridColor/MaxGridColor were themselves calibrated
    // against their captures.
    // Kept at the original, tighter calibration (from the first real
    // capture this was measured against, a "Salewa" stash-grid item) -
    // widening this to also cover a later, differently-lit real capture
    // was tried and reverted: the wider range also matched normal
    // grid-line outlines elsewhere in the image, causing this method's
    // bounding-box merge step to fuse the real highlighted cell with an
    // adjacent, unrelated empty cell into one oversized box. Since
    // FindCellAt (plain grid-line detection) is now tried FIRST and this
    // is only a fallback for when that fails, this doesn't need to cover
    // every real capture - it only needs to catch the specific case where
    // the highlight visibly disrupts the grid-line border, which was true
    // for the Salewa capture this range came from.
    private static readonly Scalar MinHighlightColorHsv = new(120, 5, 85);
    private static readonly Scalar MaxHighlightColorHsv = new(160, 20, 115);

    public static Rect? FindHighlightedCellAt(Mat screenshotBgr, OpenCvSharp.Point point, int scaledSlotSize)
    {
        using var hsv = screenshotBgr.CvtColor(ColorConversionCodes.BGR2HSV_FULL);
        using var colorFilter = hsv.InRange(MinHighlightColorHsv, MaxHighlightColorHsv);

        if (DebugDumpPath is not null)
        {
            Cv2.ImWrite(DebugDumpPath + "_highlightfilter.png", colorFilter);
        }

        var contours = Cv2.FindContoursAsArray(colorFilter, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var boxes = contours.Select(Cv2.BoundingRect).ToList();

        // Merge overlapping bounding boxes (RatEye's ParseInventoryGridHighlighted).
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                if (!boxes[i].IntersectsWith(boxes[j]))
                {
                    continue;
                }

                boxes[i] = boxes[i].Union(boxes[j]);
                boxes.RemoveAt(j);
                j = i;
            }
        }

        foreach (var box in boxes)
        {
            if (!box.Contains(point))
            {
                continue;
            }

            // Unlike RatEye's own equivalent step (which pads the box by
            // ~1/8-1/2 slot to avoid clipping the icon in their captures),
            // this app's highlight mask was measured to already trace the
            // true cell boundary tightly and accurately - padding it
            // further was verified to overshoot into a neighboring cell's
            // caption text on a real capture (a "Salewa first aid kit"
            // cell), which was enough to break matching. So the raw
            // contour box is returned as-is.
            return box;
        }

        return null;
    }
}
