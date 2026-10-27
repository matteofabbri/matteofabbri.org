using MainSite.RequestLogging;

namespace MainSite.Tests;

public sealed class ItalianRegionsTests
{
    [Fact]
    public void Ci_sono_venti_regioni_con_i_codici_istat_da_1_a_20()
    {
        Assert.Equal(20, ItalianRegions.All.Count);
        Assert.Equal(Enumerable.Range(1, 20), ItalianRegions.All.Select(r => r.Code));
    }

    [Theory]
    [InlineData("IT-21", 1)]
    [InlineData("IT-23", 2)]
    [InlineData("IT-25", 3)]
    [InlineData("IT-32", 4)]
    [InlineData("IT-34", 5)]
    [InlineData("IT-36", 6)]
    [InlineData("IT-42", 7)]
    [InlineData("IT-45", 8)]
    [InlineData("IT-52", 9)]
    [InlineData("IT-55", 10)]
    [InlineData("IT-57", 11)]
    [InlineData("IT-62", 12)]
    [InlineData("IT-65", 13)]
    [InlineData("IT-67", 14)]
    [InlineData("IT-72", 15)]
    [InlineData("IT-75", 16)]
    [InlineData("IT-77", 17)]
    [InlineData("IT-78", 18)]
    [InlineData("IT-82", 19)]
    [InlineData("IT-88", 20)]
    public void Mappa_gli_iso_3166_2_sul_codice_istat(string isoCode, int expectedCode)
    {
        Assert.True(ItalianRegions.TryFromIsoCode(isoCode, out var region));
        Assert.Equal(expectedCode, region.Code);
        Assert.Equal(ItalianRegions.FromCode(expectedCode), region);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("IT-RM")]
    [InlineData("FR-75")]
    public void Un_iso_sconosciuto_non_diventa_una_regione(string? isoCode)
    {
        Assert.False(ItalianRegions.TryFromIsoCode(isoCode, out var region));
        Assert.Equal(ItalianRegion.Unknown, region);
    }

    [Theory]
    [InlineData("Piedmont", 1)]
    [InlineData("Aosta Valley", 2)]
    [InlineData("Valle d'Aosta/Vallée d'Aoste", 2)]
    [InlineData("Lombardy", 3)]
    [InlineData("Trentino-South Tyrol", 4)]
    [InlineData("Südtirol", 4)]
    [InlineData("Tuscany", 9)]
    [InlineData("Latium", 12)]
    [InlineData("Campania", 15)]
    [InlineData("Apulia", 16)]
    [InlineData("Sicily", 19)]
    [InlineData("Sardinia", 20)]
    public void Mappa_i_nomi_inglesi_usati_da_DB_IP(string name, int expectedCode)
    {
        Assert.True(ItalianRegions.TryFromName(name, out var region));
        Assert.Equal(expectedCode, region.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Milan")]
    [InlineData("Province of Rome")]
    public void Un_nome_di_provincia_o_sconosciuto_non_e_una_regione(string? name)
    {
        Assert.False(ItalianRegions.TryFromName(name, out var region));
        Assert.Equal(ItalianRegion.Unknown, region);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Un_codice_fuori_elenco_e_sconosciuto(int code)
    {
        Assert.Equal(ItalianRegion.Unknown, ItalianRegions.FromCode(code));
    }
}
