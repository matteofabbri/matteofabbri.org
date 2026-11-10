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
        Assert.Equal(entry.UserAgent, back.UserAgent);
        Assert.Equal(entry.ClientId, back.ClientId);
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
    public async Task La_lista_pagina_le_richieste_pubbliche_dalla_piu_recente_e_salta_admin()
    {
        using var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var from = now.AddHours(-1);

        var uno = CreateEntry("req-uno") with
        {
            Timestamp = now.AddMinutes(-40),
            Path = "/blog/uno",
            CountryCode = "IT",
        };
        var due = CreateEntry("req-due") with
        {
            Timestamp = now.AddMinutes(-30),
            Path = "/blog/due",
            CountryCode = "IT",
        };
        var tre = CreateEntry("req-tre") with
        {
            Timestamp = now.AddMinutes(-20),
            Path = "/blog/tre",
            CountryCode = "US",
        };
        var admin = CreateEntry("req-admin") with
        {
            Timestamp = now.AddMinutes(-10),
            Path = "/admin/edit/bozza",
            CountryCode = "IT",
        };

        await store.InsertManyAsync([uno, due, tre, admin], CancellationToken.None);

        var prima = await store.GetPageAsync(1, 2, from);
        Assert.Equal(["req-tre", "req-due"], prima.Entries.Select(entry => entry.Id));
        Assert.Equal(3, prima.Total);
        Assert.Equal(2, prima.PageCount);
        Assert.Equal(1, prima.Page);

        var seconda = await store.GetPageAsync(2, 2, from);
        Assert.Equal(["req-uno"], seconda.Entries.Select(entry => entry.Id));
        Assert.Equal(2, seconda.Page);

        // Una pagina oltre il fondo viene riportata all'ultima.
        var oltre = await store.GetPageAsync(99, 2, from);
        Assert.Equal(2, oltre.Page);
        Assert.Single(oltre.Entries);
    }

    [Fact]
    public async Task Salva_e_pagina_i_payload_di_info_send()
    {
        using var store = CreateStore();

        var first = await store.InsertInfoAsync("payload one", "203.0.113.7", "IT", "Italy");
        Assert.NotNull(first);
        Assert.NotEqual(Guid.Empty, first.Guid);

        await Task.Delay(5);
        var second = await store.InsertInfoAsync("payload two", "198.51.100.9", "US", "United States");
        Assert.NotNull(second);

        var page1 = await store.GetInfoPageAsync(1, 1);
        Assert.Equal(2, page1.Total);
        Assert.Equal(2, page1.PageCount);
        Assert.Equal(second!.Id, Assert.Single(page1.Entries).Id);
        Assert.Equal("payload two", page1.Entries[0].Payload);
        Assert.Equal("US", page1.Entries[0].CountryCode);

        var page2 = await store.GetInfoPageAsync(2, 1);
        Assert.Equal(first!.Id, Assert.Single(page2.Entries).Id);
        Assert.Equal("IT", page2.Entries[0].CountryCode);
    }

    [Fact]
    public async Task Gestisce_i_lede_del_jumbotron()
    {
        using var store = CreateStore();

        Assert.Null(await store.AddLedeAsync("   "));

        var first = await store.AddLedeAsync("First lede");
        var second = await store.AddLedeAsync("Second lede");
        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.Equal(2, (await store.GetLedesAsync()).Count);

        var updated = await store.UpdateLedeAsync(first!.Id, "First updated");
        Assert.NotNull(updated);
        Assert.Equal("First updated", updated.Text);

        var random = await store.GetRandomLedeAsync();
        Assert.NotNull(random);
        Assert.Contains(random.Text, new[] { "First updated", "Second lede" });

        Assert.True(await store.DeleteLedeAsync(second!.Id));
        Assert.Single(await store.GetLedesAsync());
    }

    [Fact]
    public async Task Aggiorna_i_flag_di_una_riga_e_la_rilegge()
    {
        using var store = CreateStore();
        var entry = CreateEntry("req-flag") with
        {
            Timestamp = DateTimeOffset.UtcNow,
            Path = "/requests",
            QueryString = "?days=30&userAgent=" + new string('x', 400),
        };
        await store.InsertManyAsync([entry], CancellationToken.None);

        var updated = entry with
        {
            IsBot = true,
            IsMaliciousURL = false,
            BotModel = "manual",
        };

        Assert.True(await store.UpdateAsync(updated, CancellationToken.None));

        var reread = await store.GetAsync("req-flag", CancellationToken.None);
        Assert.NotNull(reread);
        Assert.True(reread.IsBot);
        Assert.False(reread.IsMaliciousURL);
        Assert.Equal("manual", reread.BotModel);
        Assert.Equal(updated.QueryString, reread.QueryString);
    }

    [Fact]
    public async Task La_prima_inizializzazione_elimina_i_vecchi_indici_full_text_una_volta()
    {
        string storage = Path.Combine(_path, "collections_storage");
        string collection = Path.Combine(storage, "00000000000000000000000000000001");
        string legacy = Path.Combine(collection, "idx", "ft", "meta");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "x"), "x");

        using (var store = CreateStore())
            await store.InsertManyAsync(
                [CreateEntry("req-ft") with { Timestamp = DateTimeOffset.UtcNow }],
                CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(collection, "idx", "ft")));
        Assert.True(File.Exists(Path.Combine(_path, "fulltext-index-dropped.json")));

        // The marker keeps the deletion off every later start.
        string recreated = Path.Combine(collection, "idx", "ft");
        Directory.CreateDirectory(recreated);
        using (var store = CreateStore())
            await store.InsertManyAsync(
                [CreateEntry("req-ft-2") with { Timestamp = DateTimeOffset.UtcNow }],
                CancellationToken.None);
        Assert.True(Directory.Exists(recreated));
    }

    [Fact]
    public async Task Aggiorna_i_flag_di_tutte_le_richieste_di_un_client_nel_periodo()
    {
        using var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        var sameOne = CreateEntry("req-client-1") with { Timestamp = now, ClientId = "aaaaaaaaaaaaaaaa" };
        var sameTwo = CreateEntry("req-client-2") with { Timestamp = now.AddMinutes(-5), ClientId = "aaaaaaaaaaaaaaaa" };
        var other = CreateEntry("req-other") with
        {
            Timestamp = now,
            ClientId = "bbbbbbbbbbbbbbbb",
            Path = "/blog/due",
        };
        var outside = CreateEntry("req-outside") with
        {
            Timestamp = now.AddDays(-10),
            ClientId = "aaaaaaaaaaaaaaaa",
        };

        await store.InsertManyAsync([sameOne, sameTwo, other, outside], CancellationToken.None);

        var entries = await store.GetPublicEntriesAsync(now.AddDays(-1));
        var update = await store.UpdateClientFlagsAsync(
            entries, "aaaaaaaaaaaaaaaa", isBot: true, isMalicious: false, CancellationToken.None);

        Assert.Equal(2, update.Matched);
        Assert.Equal(2, update.Updated);
        Assert.Equal(0, update.Skipped);
        Assert.Null(update.FirstError);
        var first = await store.GetAsync("req-client-1");
        var second = await store.GetAsync("req-client-2");
        Assert.True(first!.IsBot);
        Assert.False(first.IsMaliciousURL);
        Assert.Equal("manual", first.BotModel);
        Assert.True(second!.IsBot);
        Assert.Null((await store.GetAsync("req-other"))!.IsBot);
        Assert.Null((await store.GetAsync("req-outside"))!.IsBot);

        // Both flags back on "—": the client belongs to the model again.
        var cleared = await store.UpdateClientFlagsAsync(
            entries, "aaaaaaaaaaaaaaaa", isBot: null, isMalicious: null, CancellationToken.None);
        Assert.Equal(2, cleared.Updated);
        var afterClear = await store.GetAsync("req-client-1");
        Assert.Null(afterClear!.IsBot);
        Assert.Null(afterClear.BotModel);
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
        ClientId = "0123456789abcdef",
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
