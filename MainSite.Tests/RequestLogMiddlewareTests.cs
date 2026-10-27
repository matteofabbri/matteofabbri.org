using System.Net;
using System.Security.Claims;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class RequestLogMiddlewareTests
{
    private const string ChromeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    [Fact]
    public async Task Registra_i_dati_della_richiesta_e_della_risposta()
    {
        var sink = new CapturingSink();
        var context = CreateContext();

        var middleware = new RequestLogMiddleware(
            ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                return Task.CompletedTask;
            },
            sink,
            Options.Create(new RequestLogOptions()),
            NullLogger<RequestLogMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(sink.Entries);
        Assert.Equal("GET", entry.Method);
        Assert.Equal("https", entry.Scheme);
        Assert.Equal("matteofabbri.org", entry.Host);
        Assert.Equal("/blog", entry.Path);
        Assert.Equal("?tag=dotnet&code=***", entry.QueryString);
        Assert.Equal(StatusCodes.Status201Created, entry.StatusCode);
        Assert.True(entry.DurationMs >= 0);
        Assert.Equal("203.0.113.7", entry.RemoteIp);
        Assert.Equal(54321, entry.RemotePort);
        Assert.Equal(ChromeWindows, entry.UserAgent);
        Assert.Equal("Chrome", entry.Browser);
        Assert.Equal("126.0.0.0", entry.BrowserVersion);
        Assert.Equal("Windows 10/11", entry.OperatingSystem);
        Assert.Equal("desktop", entry.DeviceType);
        Assert.False(entry.IsBot);
        Assert.Equal("https://example.org/", entry.Referer);
        Assert.Equal("it-IT,it;q=0.9", entry.AcceptLanguage);
        Assert.Equal("text/plain", entry.ContentType);
        Assert.Equal(12L, entry.ContentLength);
        Assert.Equal("matteo@maddalena.me", entry.User);
        Assert.Equal("req-1", entry.RequestId);
        Assert.Equal("site", entry.Service);
        Assert.True(entry.IsHttps);
        Assert.False(string.IsNullOrEmpty(entry.Id));
        Assert.True(entry.Timestamp > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Normalizza_un_ip_IPv4_mappato_in_IPv6()
    {
        var sink = new CapturingSink();
        var context = CreateContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.7");

        await RunAsync(context, sink);

        Assert.Equal("203.0.113.7", Assert.Single(sink.Entries).RemoteIp);
    }

    [Fact]
    public async Task Oscura_i_valori_dei_parametri_sensibili_anche_se_la_chiave_e_codificata()
    {
        var sink = new CapturingSink();
        var context = CreateContext();
        context.Request.QueryString = new QueryString("?tag=dotnet&%63ode=abc&Access_Token=xyz&pagina=2");

        await RunAsync(context, sink);

        Assert.Equal("?tag=dotnet&%63ode=***&Access_Token=***&pagina=2", Assert.Single(sink.Entries).QueryString);
    }

    [Fact]
    public async Task Una_query_malformata_non_fa_fallire_la_richiesta()
    {
        var sink = new CapturingSink();
        var context = CreateContext();
        context.Request.QueryString = new QueryString("?%ZZ=1&a=2");

        await RunAsync(context, sink);

        Assert.Equal("?%ZZ=1&a=2", Assert.Single(sink.Entries).QueryString);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/HEALTH")]
    public async Task I_percorsi_esclusi_non_finiscono_nel_log(string path)
    {
        var sink = new CapturingSink();
        var context = CreateContext();
        context.Request.Path = path;

        await RunAsync(context, sink);

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public async Task Un_percorso_che_somiglia_a_un_health_check_ma_non_lo_e_viene_registrato()
    {
        var sink = new CapturingSink();
        var context = CreateContext();
        context.Request.Path = "/healthz";

        await RunAsync(context, sink);

        Assert.Equal("/healthz", Assert.Single(sink.Entries).Path);
    }

    [Fact]
    public async Task Se_l_endpoint_esplode_registra_500_e_rilancia()
    {
        var sink = new CapturingSink();
        var context = CreateContext();

        var middleware = new RequestLogMiddleware(
            _ => throw new InvalidOperationException("boom"),
            sink,
            Options.Create(new RequestLogOptions()),
            NullLogger<RequestLogMiddleware>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.Single(sink.Entries).StatusCode);
    }

    [Fact]
    public async Task Registra_la_regione_risolta_dall_ip()
    {
        var sink = new CapturingSink();
        var context = CreateContext();

        var middleware = new RequestLogMiddleware(
            _ => Task.CompletedTask,
            sink,
            Options.Create(new RequestLogOptions()),
            NullLogger<RequestLogMiddleware>.Instance,
            new FixedRegionResolver(ItalianRegions.FromCode(12)));

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(12, entry.RegionCode);
        Assert.Equal("Lazio", entry.RegionName);
    }

    [Fact]
    public async Task Con_il_log_disattivato_non_registra_nulla()
    {
        var sink = new CapturingSink();
        var context = CreateContext();

        var middleware = new RequestLogMiddleware(
            _ => Task.CompletedTask,
            sink,
            Options.Create(new RequestLogOptions { Enabled = false }),
            NullLogger<RequestLogMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Empty(sink.Entries);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("matteofabbri.org");
        context.Request.Path = "/blog";
        context.Request.QueryString = new QueryString("?tag=dotnet&code=segretissimo");
        context.Request.Headers.UserAgent = ChromeWindows;
        context.Request.Headers.Referer = "https://example.org/";
        context.Request.Headers.AcceptLanguage = "it-IT,it;q=0.9";
        context.Request.ContentType = "text/plain";
        context.Request.ContentLength = 12;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.Connection.RemotePort = 54321;
        context.TraceIdentifier = "req-1";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "matteo@maddalena.me")],
            authenticationType: "test"));
        return context;
    }

    private static Task RunAsync(HttpContext context, CapturingSink sink)
    {
        var middleware = new RequestLogMiddleware(
            _ => Task.CompletedTask,
            sink,
            Options.Create(new RequestLogOptions()),
            NullLogger<RequestLogMiddleware>.Instance);
        return middleware.InvokeAsync(context);
    }

    private sealed class CapturingSink : IRequestLogSink
    {
        public List<RequestLogEntry> Entries { get; } = [];

        public bool TryWrite(RequestLogEntry entry)
        {
            Entries.Add(entry);
            return true;
        }
    }

    private sealed class FixedRegionResolver(ItalianRegion region) : IRegionResolver
    {
        public ItalianRegion Resolve(IPAddress? address) => region;
    }
}
