using MainSite.RequestLogging;

namespace MainSite.Tests;

public sealed class WorldCountriesTests
{
    [Theory]
    [InlineData("IT", "Europe")]
    [InlineData("US", "North America")]
    [InlineData("BR", "South America")]
    [InlineData("JP", "Asia")]
    [InlineData("ZA", "Africa")]
    [InlineData("AU", "Oceania")]
    [InlineData("AQ", "Antarctica")]
    [InlineData("TR", "Asia")]
    [InlineData("RU", "Europe")]
    [InlineData("CY", "Europe")]
    public void Every_mapped_country_belongs_to_its_continent(string code, string continent)
    {
        Assert.Equal(continent, WorldCountries.ContinentOf(code));
    }

    [Fact]
    public void Unknown_codes_keep_a_group_of_their_own()
    {
        Assert.Equal("Unknown", WorldCountries.ContinentOf("ZZ"));
        Assert.Equal("Unknown", WorldCountries.ContinentOf(null));
        Assert.Equal("Unknown", WorldCountries.ContinentOf("  "));
        Assert.Equal("Other", WorldCountries.ContinentOf("QQ"));
    }

    [Fact]
    public void Every_country_of_the_table_has_a_continent()
    {
        Assert.All(WorldCountries.Codes, code =>
            Assert.NotEqual("Other", WorldCountries.ContinentOf(code)));
    }
}
