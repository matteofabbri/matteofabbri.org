using MainSite.RequestLogging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

// Lo store embedded: niente server, solo una cartella temporanea e il motore
// Sperone in-process. Verifica il giro completo payload + indici + rilettura.
public sealed class RequestLogStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sperone-requestlog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Scrive_e_rilegge_una_richiesta_nel_database_embedded()
    {
        using var store = CreateStore();
        var entry = CreateEntry("req-1");

        var result = await store.InsertManyAsync([entry], CancellationToken.None);

        Assert.Equal(1, result.Ok);
        Assert.Equal(0, result.Failed);

        var back = await store.GetAsync(entry.Id);
        Assert.NotNull(back);
        Assert.Equal(entry.Id, back.Id);
        Assert.Equal(entry.Path, back.Path);
        Assert.Equal(entry.QueryString, back.QueryString);
        Assert.Equal(entry.Browser, back.Browser);
        Assert.Equal(entry.RemoteIp, back.RemoteIp);
        Assert.Equal(entry.StatusCode, back.StatusCode);
    }

    [Fact]
    public async Task Un_secondo_inserimento_con_lo_stesso_id_e_un_duplicato()
    {
        using var store = CreateStore();
        var entry = CreateEntry("req-duplicata");

        var first = await store.InsertManyAsync([entry], CancellationToken.None);
        var second = await store.InsertManyAsync([entry], CancellationToken.None);

        Assert.Equal(1, first.Ok);
        Assert.Equal(0, second.Ok);
        Assert.Equal(1, second.Duplicates);
    }

    [Fact]
    public async Task Crea_la_cartella_del_database_alla_prima_scrittura()
    {
        using var store = CreateStore();

        await store.InsertManyAsync([CreateEntry("req-cartella")], CancellationToken.None);

        Assert.True(Directory.Exists(_path));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(_path));
    }

    [Fact]
    public async Task Conta_le_richieste_per_paese_nel_periodo()
    {
        using var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var from = now.AddMinutes(-1);

        var italia = CreateEntry("req-italia") with
        {
            Timestamp = now,
            CountryCode = "IT",
            CountryName = "Italia",
        };
        var statiUniti = CreateEntry("req-stati-uniti") with
        {
            Timestamp = now,
            CountryCode = "US",
            CountryName = "Stati Uniti",
        };
        var vecchia = CreateEntry("req-vecchia") with
        {
            Timestamp = now.AddDays(-2),
            CountryCode = "IT",
            CountryName = "Italia",
        };
        var senzaPaese = CreateEntry("req-senza-paese") with
        {
            Timestamp = now,
            CountryCode = "",
        };

        await store.InsertManyAsync([italia, statiUniti, vecchia, senzaPaese], CancellationToken.None);

        // La vecchia è fuori periodo; senzaPaese non ha un paese.
        Assert.Equal(3, await store.CountSinceAsync(from));
        Assert.Equal(1, await store.CountByCountryAsync("IT", from));
        Assert.Equal(1, await store.CountByCountryAsync("us", from));
        Assert.Equal(0, await store.CountByCountryAsync("FR", from));
    }

    [Fact]
    public async Task Le_ultime_richieste_saltano_l_area_admin_e_partono_dalla_piu_recente()
    {
        using var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var from = now.AddHours(-1);

        var blog = CreateEntry("req-blog") with
        {
            Timestamp = now.AddMinutes(-30),
            Path = "/blog/uno",
            CountryCode = "IT",
        };
        var admin = CreateEntry("req-admin") with
        {
            Timestamp = now.AddMinutes(-20),
            Path = "/admin/edit/bozza",
            CountryCode = "IT",
        };
        var nuova = CreateEntry("req-nuova") with
        {
            Timestamp = now.AddMinutes(-10),
            Path = "/blog/due",
            CountryCode = "US",
        };

        await store.InsertManyAsync([blog, admin, nuova], CancellationToken.None);

        var recent = await store.GetRecentAsync(5, from);

        Assert.Equal(["req-nuova", "req-blog"], recent.Select(entry => entry.Id));
        Assert.Single(await store.GetRecentAsync(1, from));
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
            // Su Windows un file dell'engine può restare aperto per un attimo:
            // il test non deve fallire per la pulizia.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private RequestLogStore CreateStore() => new(
        Options.Create(new RequestLogOptions
        {
            Enabled = true,
            Path = _path,
            Collection = "requests",
            RetentionDays = 90,
        }),
        new TestHostEnvironment(_path),
        NullLoggerFactory.Instance,
        TimeProvider.System);

    private static RequestLogEntry CreateEntry(string id) => new()
    {
        Id = id,
        Timestamp = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero),
        Method = "GET",
        Scheme = "https",
        Host = "matteofabbri.org",
        Path = "/blog",
        QueryString = "?tag=dotnet&code=***",
        Protocol = "HTTP/1.1",
        IsHttps = true,
        StatusCode = 200,
        DurationMs = 42.7,
        RemoteIp = "203.0.113.7",
        RemotePort = 54321,
        UserAgent = "Mozilla/5.0",
        Client = "Chrome 126.0.0.0 · Windows 10/11",
        Browser = "Chrome",
        BrowserVersion = "126.0.0.0",
        OperatingSystem = "Windows 10/11",
        OperatingSystemVersion = "10.0",
        DeviceType = "desktop",
        IsBot = false,
        CountryCode = "IT",
        CountryName = "Italia",
        Referer = "https://example.org/",
        AcceptLanguage = "it-IT,it;q=0.9",
        ContentType = "text/plain",
        ContentLength = 12,
        User = "matteo@maddalena.me",
        RequestId = "req-id",
        TraceId = "trace-id",
        Service = "site",
    };
}
