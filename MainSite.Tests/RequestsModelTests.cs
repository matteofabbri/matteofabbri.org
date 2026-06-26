using MainSite.Pages;
using MainSite.RequestLogging;
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

        await model.OnGetAsync(30, 1, "IT", null, null, null, default);
        Assert.Equal(3, model.PublicTotal);
        Assert.Equal(["/req-a", "/req-c", "/req-d"], model.Recent.Select(row => row.Path));
        Assert.Contains(model.ActiveFilters, filter => filter.Key == "country");

        await model.OnGetAsync(30, 1, "IT", "203.0.113.7", null, null, default);
        Assert.Equal(2, model.PublicTotal);
        Assert.Equal(["/req-a", "/req-c"], model.Recent.Select(row => row.Path));

        await model.OnGetAsync(30, 1, "IT", "203.0.113.7", "0123456789abcdef", null, default);
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
        await model.OnGetAsync(30, 1, null, null, null, "curl/8", default);

        Assert.Equal(0, model.PublicTotal);
        Assert.Empty(model.Recent);
        Assert.Single(model.ActiveFilters);
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
        string userAgent) => new()
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
    };

    private sealed class StoreProvider(RequestLogStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(RequestLogStore) ? store : null;
    }
}
