using System.Net;
using MainSite.RequestLogging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class RequestCountryResolverTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "geoip-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Senza_database_resta_sconosciuto_e_non_esplode()
    {
        using var resolver = CreateResolver();

        Assert.False(resolver.Available);
        Assert.Equal(CountryInfo.Unknown, resolver.Resolve(IPAddress.Parse("8.8.8.8")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public void Gli_ip_privati_non_hanno_un_paese(string address)
    {
        using var resolver = CreateResolver();

        Assert.Equal(CountryInfo.Unknown, resolver.Resolve(IPAddress.Parse(address)));
    }

    [Fact]
    public void Un_ip_mappato_IPv4_viene_normalizzato()
    {
        using var resolver = CreateResolver();

        Assert.Equal(CountryInfo.Unknown, resolver.Resolve(IPAddress.Parse("::ffff:192.168.1.1")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RequestCountryResolver CreateResolver() => new(
        Options.Create(new RequestLogOptions
        {
            GeoIpDatabasePath = Path.Combine(_path, "missing.mmdb"),
        }),
        new TestHostEnvironment(_path),
        NullLogger<RequestCountryResolver>.Instance);
}
