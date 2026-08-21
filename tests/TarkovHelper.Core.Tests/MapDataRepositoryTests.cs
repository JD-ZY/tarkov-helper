using TarkovHelper.Core.Maps;

namespace TarkovHelper.Core.Tests;

public class MapDataRepositoryTests
{
    // Trimmed real excerpt of the-hideout/tarkov-dev's src/data/maps.json
    // (streets-of-tarkov + customs groups), not synthetic - proves parsing
    // against genuine map calibration data.
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "maps-sample.json"));

    [Fact]
    public void ParsesInteractiveCalibrationForEachMapGroup()
    {
        var calibrations = MapDataRepository.ParseInteractiveCalibrations(LoadFixture());

        Assert.True(calibrations.ContainsKey("customs"));
        Assert.True(calibrations.ContainsKey("streets-of-tarkov"));
    }

    [Fact]
    public void SkipsNonInteractiveProjectionVariants()
    {
        // streets-of-tarkov has 5 "maps" entries (interactive, 2D, two 3D
        // variants) but only one calibration should be produced per group.
        var calibrations = MapDataRepository.ParseInteractiveCalibrations(LoadFixture());

        Assert.Single(calibrations.Where(kv => kv.Key == "streets-of-tarkov"));
    }

    [Fact]
    public void CustomsCalibration_HasCorrectTransformAndRotation()
    {
        var calibrations = MapDataRepository.ParseInteractiveCalibrations(LoadFixture());
        var customs = calibrations["customs"];

        Assert.Equal(new[] { 0.239f, 168.65f, 0.239f, 136.35f }, customs.Transform);
        Assert.Equal(180f, customs.CoordinateRotationDegrees);
        Assert.Equal("https://assets.tarkov.dev/maps/svg/Customs.svg", customs.SvgPath);
    }

    [Fact]
    public void CustomsCalibration_ParsesLayerExtents()
    {
        var calibrations = MapDataRepository.ParseInteractiveCalibrations(LoadFixture());
        var customs = calibrations["customs"];

        var secondFloor = Assert.Single(customs.Layers);
        Assert.Equal("2nd Floor", secondFloor.Name);
        var extent = Assert.Single(secondFloor.Extents);
        Assert.Equal(2.7f, extent.MinHeight);
        Assert.Equal(6.5f, extent.MaxHeight);
    }

    [Fact]
    public void CustomsCalibration_ParsesHeightRange()
    {
        var calibrations = MapDataRepository.ParseInteractiveCalibrations(LoadFixture());
        var customs = calibrations["customs"];

        Assert.NotNull(customs.HeightRange);
        Assert.Equal(-1000f, customs.HeightRange!.MinHeight);
        Assert.Equal(1000f, customs.HeightRange.MaxHeight);
    }

    [Fact]
    public void StreetsOfTarkov_ParsesMultipleLayersInOrder()
    {
        var calibrations = MapDataRepository.ParseInteractiveCalibrations(LoadFixture());
        var streets = calibrations["streets-of-tarkov"];

        Assert.Equal(5, streets.Layers.Count);
        Assert.Equal("2nd Floor", streets.Layers[0].Name);
        Assert.Equal("Underground", streets.Layers[4].Name);
    }

    [Fact]
    public void UnknownMap_LookupReturnsFalseRatherThanThrowing()
    {
        var calibrations = MapDataRepository.ParseInteractiveCalibrations(LoadFixture());

        Assert.False(calibrations.ContainsKey("some-map-that-does-not-exist"));
    }

    [Fact]
    public void CustomsCalibration_ParsesPoiLabels()
    {
        var calibrations = MapDataRepository.ParseInteractiveCalibrations(LoadFixture());
        var customs = calibrations["customs"];

        Assert.NotEmpty(customs.Pois);
        var bigRed = Assert.Single(customs.Pois, p => p.Text == "Big Red");
        Assert.Equal(-215f, bigRed.X);
        Assert.Equal(-119f, bigRed.Z);
    }
}
