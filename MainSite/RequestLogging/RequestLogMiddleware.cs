using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Options;

namespace MainSite.RequestLogging;

// Registra ogni richiesta che attraversa la pipeline. Il lavoro pesante
// (serializzazione, HTTP verso Sperone) NON avviene qui: si accoda una riga e
// si prosegue, così un database lento non rallenta il sito.
//
// Va messo subito dopo UseForwardedHeaders: a quel punto RemoteIpAddress è
// l'IP vero del client (non quello di Caddy) e la richiesta deve ancora
// attraversare static files, autenticazione ed endpoint.
public sealed class RequestLogMiddleware
{
    // Parametri che non devono finire in chiaro nei log (OAuth, sessioni,
    // chiavi API). Il valore viene sostituito con ***, la chiave resta.
    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "state", "token", "access_token", "refresh_token", "id_token",
        "password", "passwd", "secret", "client_secret", "api_key", "apikey",
        "key", "signature", "sig", "auth", "session", "sessionid",
    };

    private readonly RequestDelegate _next;
    private readonly IRequestLogSink _sink;
    private readonly IOptions<RequestLogOptions> _options;
    private readonly ILogger<RequestLogMiddleware> _logger;
    private readonly ICountryResolver? _countryResolver;

    public RequestLogMiddleware(
        RequestDelegate next,
        IRequestLogSink sink,
        IOptions<RequestLogOptions> options,
        ILogger<RequestLogMiddleware> logger,
        ICountryResolver? countryResolver = null)
    {
        _next = next;
        _sink = sink;
        _options = options;
        _logger = logger;
        _countryResolver = countryResolver;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var options = _options.Value;
        if (!options.Enabled || options.IsExcluded(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        long started = Stopwatch.GetTimestamp();
        string? traceId = Activity.Current?.TraceId.ToHexString();

        try
        {
            await _next(context);
        }
        catch
        {
            // Se l'endpoint esplode lo status non è ancora stato scritto:
            // registriamo 500 e lasciamo propagare l'eccezione al server.
            Enqueue(context, timestamp, started, StatusCodes.Status500InternalServerError, traceId);
            throw;
        }

        Enqueue(context, timestamp, started, context.Response.StatusCode, traceId);
    }

    private void Enqueue(HttpContext context, DateTimeOffset timestamp, long started, int statusCode, string? traceId)
    {
        var request = context.Request;
        var headers = request.Headers;

        string? userAgent = Truncate(headers.UserAgent.ToString(), 512);
        string? remoteIp = NormalizeIp(context.Connection.RemoteIpAddress);
        var country = _countryResolver?.Resolve(context.Connection.RemoteIpAddress) ?? CountryInfo.Unknown;

        var entry = new RequestLogEntry
        {
            Id = NewId(timestamp),
            Timestamp = timestamp,

            Method = request.Method,
            Scheme = request.Scheme,
            Host = Truncate(request.Host.Value, 255) ?? "",
            Path = Truncate(request.Path.Value, 512) ?? "/",
            QueryString = SanitizeQuery(request.QueryString.Value),
            Protocol = request.Protocol,
            IsHttps = request.IsHttps,

            StatusCode = statusCode,
            DurationMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3),

            RemoteIp = remoteIp,
            RemotePort = context.Connection.RemotePort > 0 ? context.Connection.RemotePort : null,
            ForwardedFor = Truncate(headers["X-Forwarded-For"].ToString(), 512),

            UserAgent = userAgent,
            ClientId = ClientIds.For(remoteIp, userAgent),

            CountryCode = country.Code,
            CountryName = country.IsKnown ? country.Name : null,

            Referer = Truncate(headers.Referer.ToString(), 512),
            AcceptLanguage = Truncate(headers.AcceptLanguage.ToString(), 128),

            ContentType = Truncate(request.ContentType, 128),
            ContentLength = request.ContentLength,

            // Dopo next() l'autenticazione ha già popolato User, se l'endpoint
            // l'ha richiesta. Non leggiamo cookie né header Authorization.
            User = context.User.Identity?.IsAuthenticated == true
                ? Truncate(context.User.Identity.Name, 255)
                : null,
            RequestId = context.TraceIdentifier,
            TraceId = traceId,

            Service = "site",
        };

        if (!_sink.TryWrite(entry))
            _logger.LogDebug("Coda del log richieste piena: richiesta {Method} {Path} scartata", request.Method, request.Path);
    }

    // ItemId ordinabile per tempo: tick UTC (19 cifre) + suffisso casuale per
    // evitare collisioni tra richieste nello stesso tick.
    private static string NewId(DateTimeOffset timestamp) => $"{timestamp.UtcTicks:D19}-{Guid.NewGuid():N}";

    private static string? NormalizeIp(IPAddress? address)
    {
        if (address is null)
            return null;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.ToString();
    }

    // Toglie il valore ai parametri sensibili mantenendo la query leggibile.
    // Esempio: ?code=abc&x=1 -> ?code=***&x=1
    private static string? SanitizeQuery(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var parts = query.TrimStart('?').Split('&');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            int equals = part.IndexOf('=');
            string key = equals < 0 ? part : part[..equals];
            string decodedKey = Uri.UnescapeDataString(key.Replace('+', ' '));

            if (SensitiveQueryKeys.Contains(decodedKey))
                parts[i] = key + "=***";
        }

        string result = "?" + string.Join("&", parts);
        return Truncate(result, 1_024);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
