using TarkovHelper.Core.Maps;

namespace TarkovHelper.Core.Tests;

public class MapNameResolverTests
{
    [Theory]
    [InlineData("bigmap", "customs")]
    [InlineData("factory4_day", "factory")]
    [InlineData("factory4_night", "factory")]
    [InlineData("Woods", "woods")]
    [InlineData("Shoreline", "shoreline")]
    [InlineData("Interchange", "interchange")]
    [InlineData("RezervBase", "reserve")]
    [InlineData("Lighthouse", "lighthouse")]
    [InlineData("TarkovStreets", "streets-of-tarkov")]
    [InlineData("laboratory", "the-lab")]
    [InlineData("Sandbox", "ground-zero")]
    [InlineData("Sandbox_high", "ground-zero")]
    [InlineData("Labyrinth", "the-labyrinth")]
    public void ResolvesKnownNameIds(string nameId, string expectedNormalizedName)
    {
        Assert.Equal(expectedNormalizedName, MapNameResolver.ToNormalizedName(nameId));
    }

    [Fact]
    public void UnknownNameId_ReturnsNull()
    {
        Assert.Null(MapNameResolver.ToNormalizedName("some_unknown_map"));
    }

    [Fact]
    public void LookupIsCaseSensitive_RezervBaseDoesNotMatchLowercase()
    {
        // SPT's internal Id field is capitalized "RezervBase" while the
        // tarkov.dev normalizedName/folder convention is lowercase - these
        // are two different strings and must not be conflated.
        Assert.Null(MapNameResolver.ToNormalizedName("rezervbase"));
    }
}
