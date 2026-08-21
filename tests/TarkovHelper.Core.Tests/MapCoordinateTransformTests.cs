using TarkovHelper.Core.Maps;

namespace TarkovHelper.Core.Tests;

public class MapCoordinateTransformTests
{
    // Real calibration constants for Customs, verified against
    // the-hideout/tarkov-dev src/data/maps.json, cross-checked against the
    // actual downloaded SVG's viewBox (1062.4827 x 535.17401).
    private static MapCalibration CustomsCalibration => new()
    {
        NormalizedName = "customs",
        Transform = [0.239f, 168.65f, 0.239f, 136.35f],
        CoordinateRotationDegrees = 180f,
        Bounds = [[698f, -307f], [-372f, 237f]],
    };

    private const float CustomsViewBoxWidth = 1062.4827f;
    private const float CustomsViewBoxHeight = 535.17401f;

    private static MapCalibration WoodsCalibration => new()
    {
        NormalizedName = "woods",
        Transform = [0.1855f, 112.95f, 0.1855f, 167.85f],
        CoordinateRotationDegrees = 180f,
        Bounds = [[646f, -914f], [-761f, 442f]],
    };

    private const float WoodsViewBoxWidth = 1472.7926f;
    private const float WoodsViewBoxHeight = 1420.5995f;

    // Ground truth computed by actually EXECUTING tarkov-dev's real Leaflet
    // code (Node.js + jsdom + the real `leaflet` npm package), specifically
    // constructing a literal `new L.Bounds(nwPoint, sePoint)` and reading
    // its real .min/.max/.getSize() - NOT assuming nwPoint is the top-left
    // pixel by naming convention. An earlier version of this test suite
    // encoded values from a version of the formula that skipped this step
    // (used nwPoint/sePoint directly as remap endpoints); those values
    // were self-consistent but wrong, and were only caught by cross-
    // checking against two real screenshots from the user's own machine
    // with known real-world locations (top-right of Customs, top-left and
    // bottom-right of Woods) - both maps' `L.Bounds` min/max ends up
    // reordering the Y axis relative to raw NW/SE order, which the earlier
    // version silently got wrong for every map without producing an error.
    [Theory]
    [InlineData("Dorms", 200f, 150f, 494.501f, 449.586f)]
    [InlineData("Big Red", -215f, -119f, 906.6f, 184.9f)]
    public void WorldToSvgPixel_MatchesLeafletExecutedBoundsNormalization(
        string _, float worldX, float worldZ, float expectedPixelX, float expectedPixelY)
    {
        var (pixelX, pixelY) = MapCoordinateTransform.WorldToSvgPixel(
            worldX, worldZ, CustomsCalibration, CustomsViewBoxWidth, CustomsViewBoxHeight);

        Assert.Equal(expectedPixelX, pixelX, precision: 1);
        Assert.Equal(expectedPixelY, pixelY, precision: 1);
    }

    // Real screenshot from the user's own machine, filename
    // "2026-08-09[01-47]_-322.87, 2.42, -216.13_..." - user confirmed they
    // had spawned at the top-right of Customs when this was taken.
    [Fact]
    public void WorldToSvgPixel_RealUserScreenshot_LandsInTopRightOfCustoms()
    {
        var (pixelX, pixelY) = MapCoordinateTransform.WorldToSvgPixel(
            -322.87f, -216.13f, CustomsCalibration, CustomsViewBoxWidth, CustomsViewBoxHeight);

        var fracX = pixelX / CustomsViewBoxWidth;
        var fracY = pixelY / CustomsViewBoxHeight;

        Assert.True(fracX > 0.75f, $"Expected far right (>75%), got {fracX:P1}");
        Assert.True(fracY < 0.25f, $"Expected near top (<25%), got {fracY:P1}");
    }

    // Real screenshot from the user's own machine, filename
    // "2026-08-09[01-08]_-186.26, -0.84, 200.62_..." - user confirmed
    // (after cross-checking the live tarkov.dev map) they were standing
    // near Military Camp, which is near the SOUTH of Woods, just north of
    // RUAF Gate/Roadblock - not near the north as first assumed from a
    // misread screenshot.
    [Fact]
    public void WorldToSvgPixel_RealUserScreenshot_LandsNearSouthOfWoods()
    {
        var (pixelX, pixelY) = MapCoordinateTransform.WorldToSvgPixel(
            -186.26f, 200.62f, WoodsCalibration, WoodsViewBoxWidth, WoodsViewBoxHeight);

        var fracY = pixelY / WoodsViewBoxHeight;

        Assert.True(fracY > 0.75f, $"Expected near bottom/south (>75%), got {fracY:P1}");
    }

    [Fact]
    public void WoodsCultistVillage_KnownFarNorth_LandsNearTopOfViewBox()
    {
        var (_, pixelY) = MapCoordinateTransform.WorldToSvgPixel(
            -80f, -680f, WoodsCalibration, WoodsViewBoxWidth, WoodsViewBoxHeight);

        Assert.True(pixelY / WoodsViewBoxHeight < 0.25f);
    }

    [Fact]
    public void WoodsMilitaryCamp_KnownSouthNearRuafGate_LandsNearBottomOfViewBox()
    {
        var (_, pixelY) = MapCoordinateTransform.WorldToSvgPixel(
            -188f, 235f, WoodsCalibration, WoodsViewBoxWidth, WoodsViewBoxHeight);

        Assert.True(pixelY / WoodsViewBoxHeight > 0.75f);
    }

    [Fact]
    public void MissingTransform_ThrowsRatherThanSilentlyMisplacingMarker()
    {
        var calibration = new MapCalibration { NormalizedName = "unknown-map", Transform = null };

        Assert.Throws<InvalidOperationException>(() =>
            MapCoordinateTransform.WorldToSvgPixel(0f, 0f, calibration, 100f, 100f));
    }

    [Fact]
    public void MissingBounds_ThrowsRatherThanSilentlyMisplacingMarker()
    {
        var calibration = new MapCalibration
        {
            NormalizedName = "unknown-map",
            Transform = [1f, 0f, 1f, 0f],
            Bounds = null,
        };

        Assert.Throws<InvalidOperationException>(() =>
            MapCoordinateTransform.WorldToSvgPixel(0f, 0f, calibration, 100f, 100f));
    }

    [Fact]
    public void SelectLayer_WithinExtentRange_ReturnsMatchingLayer()
    {
        var calibration = new MapCalibration
        {
            Layers =
            [
                new MapLayer
                {
                    Name = "2nd Floor",
                    Extents = [new MapHeightExtent { MinHeight = 2.7f, MaxHeight = 6.5f }],
                },
            ],
        };

        var layer = MapCoordinateTransform.SelectLayer(4.0f, calibration);

        Assert.NotNull(layer);
        Assert.Equal("2nd Floor", layer!.Name);
    }

    [Fact]
    public void SelectLayer_OutsideAllExtents_ReturnsNull()
    {
        var calibration = new MapCalibration
        {
            Layers =
            [
                new MapLayer
                {
                    Name = "2nd Floor",
                    Extents = [new MapHeightExtent { MinHeight = 2.7f, MaxHeight = 6.5f }],
                },
            ],
        };

        var layer = MapCoordinateTransform.SelectLayer(0.0f, calibration);

        Assert.Null(layer);
    }

    [Fact]
    public void SelectLayer_NoLayers_ReturnsNull()
    {
        var calibration = new MapCalibration();

        var layer = MapCoordinateTransform.SelectLayer(4.0f, calibration);

        Assert.Null(layer);
    }
}
