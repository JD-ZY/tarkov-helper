using TarkovHelper.Core.Position;

namespace TarkovHelper.Core.Tests;

public class ScreenshotFilenameParserTests
{
    // Real sample filename found during research, matching the format
    // TarkovMonitor's production regex parses.
    private const string RealSampleFilename =
        "2025-12-25[10-14]_-519.33, -39.61, 68.41_-0.04164, 0.80479, -0.05690, -0.58935_5.68 (0).png";

    [Fact]
    public void ParsesPositionFromRealSampleFilename()
    {
        var success = ScreenshotFilenameParser.TryParse(RealSampleFilename, out var position);

        Assert.True(success);
        Assert.Equal(-519.33f, position.X, precision: 2);
        Assert.Equal(-39.61f, position.Y, precision: 2);
        Assert.Equal(68.41f, position.Z, precision: 2);
    }

    [Fact]
    public void YawIsFiniteAndWithinDegreeRange()
    {
        ScreenshotFilenameParser.TryParse(RealSampleFilename, out var position);

        Assert.False(float.IsNaN(position.YawDegrees));
        Assert.InRange(position.YawDegrees, -180f, 180f);
    }

    [Theory]
    [InlineData("not-a-screenshot.png")]
    [InlineData("2025-12-25[10-14].png")]
    [InlineData("random-file.txt")]
    public void NonMatchingFilenames_FailToParse(string filename)
    {
        var success = ScreenshotFilenameParser.TryParse(filename, out _);

        Assert.False(success);
    }

    // Actual filenames from a real EFT session (Documents\Escape From
    // Tarkov\Screenshots on this machine), not synthetic - confirms the
    // parser works against genuine game output, not just the one sample
    // found during research.
    [Theory]
    [InlineData(
        "2026-08-08[17-53]_-312.09, 1.25, -210.94_0.01107, -0.10934, 0.00122, 0.99394_13.29 (0).png",
        -312.09f, 1.25f, -210.94f)]
    [InlineData(
        "2026-08-08[17-54]_-292.46, 1.90, -200.45_-0.02063, -0.62836, 0.01641, -0.77748_13.31 (0).png",
        -292.46f, 1.90f, -200.45f)]
    public void ParsesPositionFromRealUserScreenshotFilenames(string filename, float expectedX, float expectedY, float expectedZ)
    {
        var success = ScreenshotFilenameParser.TryParse(filename, out var position);

        Assert.True(success);
        Assert.Equal(expectedX, position.X, precision: 2);
        Assert.Equal(expectedY, position.Y, precision: 2);
        Assert.Equal(expectedZ, position.Z, precision: 2);
        Assert.False(float.IsNaN(position.YawDegrees));
    }

    [Fact]
    public void ParsingUsesInvariantCulture_NotAffectedByCurrentCulture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            // German locale uses ',' as decimal separator - if the parser
            // ever regressed to using CurrentCulture instead of
            // InvariantCulture, this would throw or silently misparse.
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var success = ScreenshotFilenameParser.TryParse(RealSampleFilename, out var position);

            Assert.True(success);
            Assert.Equal(-519.33f, position.X, precision: 2);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
