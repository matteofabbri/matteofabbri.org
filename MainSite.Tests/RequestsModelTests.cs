using System.Text;
using System.Text.Json;
using MainSite.Pages;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class RequestsModelTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "sperone-requests-model-" + Guid.NewGuid().ToString("N"));
    private readonly RequestLogOptions _options = new()
    {
        Enabled = true,
        Path = Path.Combine(Path.GetTempPath(), "unused"),
        Collection = "requests",
        RetentionDays = 90,
    };
    private readonly RequestLogStore _store;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public RequestsModelTests()
    {
        _options.Path = _path;
        _store = new RequestLogStore(
            Options.Create(_options),
            new TestHostEnvironment(_path),
            NullLoggerFactory.Instance,
            TimeProvider.System);
    }

    [Fact]
    public async Task Filters_stack_and_are_reflected_in_the_list_and_route()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-a", now.AddMinutes(-1), "IT", "203.0.113.7", "0123456789abcdef", "Mozilla/5.0"),
            Entry("req-b", now.AddMinutes(-2), "US", "198.51.100.9", "fedcba9876543210", "curl/8"),
            Entry("req-c", now.AddMinutes(-3), "IT", "203.0.113.7", "1111111111111111", "curl/8"),
            Entry("req-d", now.AddMinutes(-4), "IT", "203.0.113.8", "2222222222222222", "Mozilla/5.0"),
        ], default);

        var model = CreateModel();

        await model.OnGetAsync(30, 1, "IT", null, null, null, null, null, default);
        Assert.Equal(3, model.PublicTotal);
        Assert.Equal(["/req-a", "/req-c", "/req-d"], model.Recent.Select(row => row.Path));
        Assert.Contains(model.ActiveFilters, filter => filter.Key == "country");

        await model.OnGetAsync(30, 1, "IT", "203.0.113.7", null, null, null, null, default);
        Assert.Equal(2, model.PublicTotal);
        Assert.Equal(["/req-a", "/req-c"], model.Recent.Select(row => row.Path));

        await model.OnGetAsync(30, 1, "IT", "203.0.113.7", "0123456789abcdef", null, null, null, default);
        Assert.Equal(1, model.PublicTotal);
        Assert.Equal(["/req-a"], model.Recent.Select(row => row.Path));
        Assert.Equal(3, model.ActiveFilters.Count);

        var route = model.FilterRoute("ip", "203.0.113.8");
        Assert.Equal("IT", route["country"]);
        Assert.Equal("203.0.113.8", route["ip"]);
        Assert.Equal("0123456789abcdef", route["clientId"]);
        Assert.Equal("1", route["pageNumber"]);

        var removed = model.RemoveFilterRoute("country");
        Assert.False(removed.ContainsKey("country"));
        Assert.Equal("203.0.113.7", removed["ip"]);
        Assert.Equal("0123456789abcdef", removed["clientId"]);

        var pageTwo = model.PageRoute(2);
        Assert.Equal("IT", pageTwo["country"]);
        Assert.Equal("203.0.113.7", pageTwo["ip"]);
        Assert.Equal("0123456789abcdef", pageTwo["clientId"]);
        Assert.Equal("2", pageTwo["pageNumber"]);
    }

    [Fact]
    public async Task A_user_agent_filter_can_make_a_non_empty_view_empty()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-a", now.AddMinutes(-1), "IT", "203.0.113.7", "0123456789abcdef", "Mozilla/5.0"),
        ], default);

        var model = CreateModel();
        await model.OnGetAsync(30, 1, null, null, null, "curl/8", null, null, default);

        Assert.Equal(0, model.PublicTotal);
        Assert.Empty(model.Recent);
        Assert.Single(model.ActiveFilters);
    }

    [Fact]
    public async Task The_visitor_chart_counts_distinct_clients_per_day()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-a", now, "IT", "203.0.113.7", "0123456789abcdef", "Mozilla/5.0"),
            Entry("req-b", now.AddMinutes(-1), "IT", "203.0.113.7", "0123456789abcdef", "Mozilla/5.0"),
            Entry("req-c", now.AddMinutes(-2), "IT", "203.0.113.8", "1111111111111111", "curl/8"),
            Entry("req-d", now.AddDays(-2), "IT", "203.0.113.8", "1111111111111111", "curl/8"),
        ], default);

        var model = CreateModel();
        await model.OnGetAsync(30, 1, null, null, null, null, null, null, default);

        Assert.Equal(2, model.UniqueVisitors);
        Assert.True(model.VisitorBuckets.Count >= 30);
        Assert.Equal(model.VisitorBuckets, model.VisitorBuckets.OrderBy(bucket => bucket.Start));
        Assert.Equal(2, model.VisitorBuckets[^1].Visitors);
        Assert.Equal(3, model.VisitorBuckets[^1].Requests);

        var rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
        var twoDaysAgo = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(now.AddDays(-2), rome).DateTime);
        Assert.Equal(1, model.VisitorBuckets.Single(bucket =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bucket.Start, rome).DateTime) == twoDaysAgo).Visitors);

        var points = model.ChartPoints();
        Assert.Equal(model.VisitorBuckets.Count, points.Count);
        Assert.Contains(points, point => point.Visitors == 2 && point.Requests == 3);
        Assert.Contains("\"visitors\":", model.ChartJson());

        await model.OnGetAsync(30, 1, null, null, "0123456789abcdef", null, null, null, default);
        Assert.Equal(1, model.UniqueVisitors);
        Assert.Equal(1, model.VisitorBuckets[^1].Visitors);
    }

    [Fact]
    public async Task The_default_period_is_the_last_24_hours_with_hourly_buckets()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-a", now.AddMinutes(-1), "IT", "203.0.113.7", "aaaaaaaaaaaaaaaa", "Mozilla/5.0"),
            Entry("req-b", now.AddHours(-6), "US", "198.51.100.9", "bbbbbbbbbbbbbbbb", "curl/8"),
            Entry("req-c", now.AddHours(-8), "IT", "203.0.113.8", "cccccccccccccccc", "curl/8"),
            Entry("req-d", now.AddDays(-2), "IT", "203.0.113.8", "dddddddddddddddd", "curl/8"),
        ], default);

        var model = CreateModel();
        await model.OnGetAsync(null, 1, null, null, null, null, null, null, default);

        Assert.Equal(1, model.Days);
        Assert.Equal("last 24 hours", model.PeriodLabel);
        Assert.True(model.HourlyPeriod);
        Assert.Equal("Requests, bots and visitors per hour", model.ChartTitle);

        // Barre orarie che coprono tutta la finestra: 24, più l'ora parziale in corso.
        Assert.InRange(model.VisitorBuckets.Count, 24, 25);
        Assert.All(model.VisitorBuckets, bucket => Assert.Equal(TimeSpan.FromHours(1), bucket.Duration));
        Assert.Equal(model.VisitorBuckets, model.VisitorBuckets.OrderBy(bucket => bucket.Start));

        // La richiesta di due giorni fa resta fuori dalla finestra.
        Assert.Equal(3, model.TotalRequests);
        Assert.Equal(3, model.PublicTotal);
        Assert.Equal(3, model.Recent.Count);
        Assert.Equal(3, model.UniqueVisitors);
        Assert.Equal(3, model.VisitorBuckets.Sum(bucket => bucket.Visitors));

        var points = model.ChartPoints();
        Assert.Equal(model.VisitorBuckets.Count, points.Count);
        Assert.All(points, point => Assert.Equal(5, point.Label.Length));
    }

    [Fact]
    public async Task The_csv_export_contains_the_filtered_rows()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-a", now.AddMinutes(-1), "IT", "203.0.113.7", "0123456789abcdef", "Mozilla/5.0, \"quoted\""),
            Entry("req-b", now.AddMinutes(-2), "US", "198.51.100.9", "fedcba9876543210", "curl/8"),
        ], default);

        var model = CreateModel();
        var result = Assert.IsType<FileContentResult>(
            await model.OnGetCsvAsync(30, "IT", null, null, null, null, null, default));

        Assert.Equal("text/csv", result.ContentType?.Split(';')[0]);
        Assert.EndsWith(".csv", result.FileDownloadName);

        string csv = Encoding.UTF8.GetString(result.FileContents);
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains(
            "When,Method,Path,Status,Country,Country code,IP,Client ID,User-Agent,Duration (ms)",
            csv);
        Assert.Contains("/req-a", csv);
        Assert.Contains("Italy,IT,203.0.113.7,0123456789abcdef", csv);
        Assert.DoesNotContain("/req-b", csv);
        // virgole e virgolette dello User-Agent restano un solo campo CSV
        Assert.Contains("\"Mozilla/5.0, \"\"quoted\"\"\"", csv);
    }

    [Fact]
    public async Task The_csv_export_neutralises_formula_cells()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-a", now.AddMinutes(-1), "IT", "203.0.113.7", "0123456789abcdef", "=HYPERLINK(\"https://example.com\")"),
        ], default);

        var model = CreateModel();
        var result = Assert.IsType<FileContentResult>(
            await model.OnGetCsvAsync(30, null, null, null, null, null, null, default));

        string csv = Encoding.UTF8.GetString(result.FileContents);
        Assert.Contains("'=HYPERLINK", csv);
    }

    public void Dispose()
    {
        _store.Dispose();
        _cache.Dispose();
        try
        {
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task The_chart_counts_bots_and_malicious_rows_per_bucket()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-bot", now.AddMinutes(-1), "IT", "203.0.113.7", "aaaaaaaaaaaaaaaa", "Mozilla/5.0", isBot: true),
            Entry("req-mal", now.AddMinutes(-2), "IT", "203.0.113.8", "bbbbbbbbbbbbbbbb", "curl/8", isBot: true, isMalicious: true),
            Entry("req-human", now.AddMinutes(-3), "IT", "203.0.113.9", "cccccccccccccccc", "Mozilla/5.0", isBot: false, isMalicious: false),
            Entry("req-unknown", now.AddMinutes(-4), "IT", "203.0.113.10", "dddddddddddddddd", "curl/8"),
        ], default);

        var model = CreateModel();
        await model.OnGetAsync(1, 1, null, null, null, null, null, null, default);

        var points = model.ChartPoints();
        Assert.Equal(2, points.Sum(point => point.Bots));
        Assert.Equal(1, points.Sum(point => point.Malicious));
        Assert.Equal(2, model.TotalBots);
        Assert.Equal(1, model.TotalMalicious);

        using var document = JsonDocument.Parse(model.ChartJson());
        JsonElement root = document.RootElement;
        Assert.Equal(2, root.GetProperty("bots").EnumerateArray().Sum(value => value.GetInt32()));
        Assert.Equal(1, root.GetProperty("malicious").EnumerateArray().Sum(value => value.GetInt32()));
    }

    [Fact]
    public async Task Every_number_on_the_page_counts_the_same_public_rows()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-bot", now.AddMinutes(-1), "IT", "203.0.113.7", "aaaaaaaaaaaaaaaa", "curl/8", isBot: true),
            Entry("req-human", now.AddMinutes(-2), "IT", "203.0.113.9", "cccccccccccccccc", "Mozilla/5.0", isBot: false),
            Entry("req-admin-bot", now.AddMinutes(-3), "IT", "203.0.113.8", "bbbbbbbbbbbbbbbb", "curl/8", isBot: true)
                with { Path = "/admin/requests" },
        ], default);

        var model = CreateModel();
        await model.OnGetAsync(1, 1, null, null, null, null, null, null, default);

        // The /admin row is invisible everywhere on the page: the summary, the
        // country map, the chart and the list agree on the two public rows.
        Assert.Equal(2, model.TotalRequests);
        Assert.Equal(2, model.PublicTotal);
        Assert.Equal(2, model.GeocodedRequests);
        Assert.Equal(0, model.UnknownRequests);
        Assert.Equal(1, model.TotalBots);
        Assert.Equal(2, model.Recent.Count);
        Assert.DoesNotContain(model.Recent, row => row.Path.StartsWith("/admin", StringComparison.Ordinal));

        var points = model.ChartPoints();
        Assert.Equal(model.TotalRequests, points.Sum(point => point.Requests));
        Assert.Equal(model.TotalBots, points.Sum(point => point.Bots));

        // The bot filter is the same rows again: its number must equal the bot
        // total the page prints next to the chart.
        var filtered = CreateModel();
        await filtered.OnGetAsync(1, 1, null, null, null, null, "true", null, default);
        Assert.Equal(model.TotalBots, filtered.TotalRequests);
        Assert.Equal(filtered.TotalRequests, filtered.ChartPoints().Sum(point => point.Bots));
    }

    [Fact]
    public async Task The_bot_and_malicious_filters_narrow_the_list_and_toggle_as_chips()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-bot", now.AddMinutes(-1), "IT", "203.0.113.7", "aaaaaaaaaaaaaaaa", "Mozilla/5.0", isBot: true),
            Entry("req-mal", now.AddMinutes(-2), "IT", "203.0.113.7", "bbbbbbbbbbbbbbbb", "curl/8", isBot: true, isMalicious: true),
            Entry("req-human", now.AddMinutes(-3), "IT", "203.0.113.8", "cccccccccccccccc", "Mozilla/5.0", isBot: false, isMalicious: false),
            Entry("req-unknown", now.AddMinutes(-4), "IT", "203.0.113.8", "dddddddddddddddd", "curl/8"),
        ], default);

        var model = CreateModel();

        await model.OnGetAsync(30, 1, null, null, null, null, "true", null, default);
        Assert.Equal(2, model.PublicTotal);
        Assert.Equal(["/req-bot", "/req-mal"], model.Recent.Select(row => row.Path));
        Assert.Contains(model.ActiveFilters, filter => filter.Key == "bot" && filter.Label == "Bot: yes");

        await model.OnGetAsync(30, 1, null, null, null, null, "false", "false", default);
        Assert.Equal(1, model.PublicTotal);
        Assert.Equal(["/req-human"], model.Recent.Select(row => row.Path));
        Assert.Contains(model.ActiveFilters, filter => filter.Label == "Malicious: no");

        // Unknown rows belong to no flag filter: "no" means the model said so.
        await model.OnGetAsync(30, 1, null, null, null, null, "false", null, default);
        Assert.Equal(1, model.PublicTotal);

        // A filter value the page does not recognise is ignored.
        await model.OnGetAsync(30, 1, null, null, null, null, "maybe", null, default);
        Assert.Equal(4, model.PublicTotal);

        // The badge link toggles: the active state removes the filter.
        var set = model.ToggleFilterRoute("bot", "true");
        Assert.Equal("true", set["bot"]);
        Assert.Equal("1", set["pageNumber"]);

        model = CreateModel();
        await model.OnGetAsync(30, 1, null, null, null, null, "true", null, default);
        var removed = model.ToggleFilterRoute("bot", "true");
        Assert.False(removed.ContainsKey("bot"));

        var replaced = model.ToggleFilterRoute("bot", "false");
        Assert.Equal("false", replaced["bot"]);
    }

    [Fact]
    public async Task The_country_list_is_grouped_by_continent_with_the_fullest_first()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.InsertManyAsync(
        [
            Entry("req-a", now.AddMinutes(-1), "IT", "203.0.113.7", "aaaaaaaaaaaaaaaa", "Mozilla/5.0"),
            Entry("req-b", now.AddMinutes(-2), "IT", "203.0.113.7", "aaaaaaaaaaaaaaaa", "Mozilla/5.0"),
            Entry("req-c", now.AddMinutes(-3), "US", "198.51.100.9", "bbbbbbbbbbbbbbbb", "curl/8"),
            Entry("req-d", now.AddMinutes(-4), "JP", "198.51.100.10", "cccccccccccccccc", "curl/8"),
            Entry("req-e", now.AddMinutes(-5), "ZZ", "203.0.113.9", "dddddddddddddddd", "curl/8"),
        ], default);

        var model = CreateModel();
        await model.OnGetAsync(30, 1, null, null, null, null, null, null, default);

        // The aggregate counts come from the country index, which has no
        // bucket for unknown ("ZZ"): those rows only show in the filtered view.
        Assert.Equal(
            ["Europe", "Asia", "North America"],
            model.ContinentGroups.Select(group => group.Name));
        Assert.Equal(2, model.ContinentGroups[0].Total);
        Assert.Equal(["Italy"], model.ContinentGroups[0].Countries.Select(country => country.Name));

        var northAmerica = model.ContinentGroups.Single(group => group.Name == "North America");
        Assert.Equal("United States of America", northAmerica.Countries.Single().Name);
        Assert.Equal(1, northAmerica.Total);
    }

    private RequestsModel CreateModel() => new(
        new StoreProvider(_store),
        Options.Create(_options),
        _cache,
        NullLogger<RequestsModel>.Instance);

    private static RequestLogEntry Entry(
        string id,
        DateTimeOffset timestamp,
        string country,
        string ip,
        string clientId,
        string userAgent,
        bool? isBot = null,
        bool? isMalicious = null) => new()
    {
        Id = id,
        Timestamp = timestamp,
        Method = "GET",
        Scheme = "https",
        Host = "matteofabbri.org",
        Path = "/" + id,
        Protocol = "HTTP/1.1",
        IsHttps = true,
        StatusCode = 200,
        DurationMs = 12.3,
        RemoteIp = ip,
        UserAgent = userAgent,
        ClientId = clientId,
        CountryCode = country,
        Service = "site",
        IsBot = isBot,
        IsMaliciousURL = isMalicious,
    };

    private sealed class StoreProvider(RequestLogStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(RequestLogStore) ? store : null;
    }
}
