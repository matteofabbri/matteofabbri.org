using System.Net;
using System.Net.Sockets;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MainSite.RequestLogging;

// Paese di un IP: codice ISO 3166-1 alpha-2 e nome. Code vuoto = privato,
// non geolocalizzato o database assente.
public readonly record struct CountryInfo(string Code, string Name)
{
    public static readonly CountryInfo Unknown = new("", "");
    public bool IsKnown => Code.Length > 0;
}

// Astrazione minima per poter provare il middleware senza un database GeoIP.
public interface ICountryResolver
{
    CountryInfo Resolve(IPAddress? address);
}

// Risolve un IP nel paese usando un database MMDB locale (DB-IP Lite City o
// GeoLite2 City). Nessuna chiamata a servizi esterni: l'IP del visitatore non
// esce dal server.
public sealed class RequestCountryResolver : ICountryResolver, IDisposable
{
    private readonly RequestLogOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<RequestCountryResolver> _logger;
    private readonly Lazy<DatabaseReader?> _reader;

    public RequestCountryResolver(
        IOptions<RequestLogOptions> options,
        IHostEnvironment environment,
        ILogger<RequestCountryResolver> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _reader = new Lazy<DatabaseReader?>(Open, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string ResolvedDatabasePath
    {
        get
        {
            string configured = _options.EffectiveGeoIpDatabasePath;
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(_environment.ContentRootPath, configured);
        }
    }

    public bool Available => _reader.Value is not null;

    public DateTimeOffset? LastUpdatedUtc =>
        File.Exists(ResolvedDatabasePath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(ResolvedDatabasePath), TimeSpan.Zero)
            : null;

    public CountryInfo Resolve(IPAddress? address)
    {
        if (address is null)
            return CountryInfo.Unknown;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IsPrivate(address))
            return CountryInfo.Unknown;

        var reader = _reader.Value;
        if (reader is null)
            return CountryInfo.Unknown;

        try
        {
            if (!reader.TryCity(address, out var city) || city is null)
                return CountryInfo.Unknown;

            string? code = city.Country.IsoCode;
            if (string.IsNullOrWhiteSpace(code))
                return CountryInfo.Unknown;

            string normalized = code.Trim().ToUpperInvariant();
            string? name = city.Country.Name;
            return new CountryInfo(
                normalized,
                string.IsNullOrWhiteSpace(name) ? WorldCountries.NameOf(normalized) : name.Trim());
        }
        catch (Exception ex)
        {
            // Indirizzo non presente nel database o record corrotto: per il
            // log vale "sconosciuto", non deve far fallire la richiesta.
            _logger.LogDebug(ex, "GeoIP: risoluzione fallita per {Address}", address);
            return CountryInfo.Unknown;
        }
    }

    public void Dispose()
    {
        if (_reader.IsValueCreated)
            _reader.Value?.Dispose();
    }

    private DatabaseReader? Open()
    {
        string path = ResolvedDatabasePath;
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "GeoIP non configurato: database non trovato in {Path}. La mappa richieste resterà vuota finché non lo scarichi (deploy/update-geoip.sh o deploy/update-geoip.ps1).",
                path);
            return null;
        }

        try
        {
            var reader = new DatabaseReader(path);
            _logger.LogInformation("GeoIP: database caricato da {Path}", path);
            return reader;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GeoIP: impossibile aprire il database {Path}", path);
            return null;
        }
    }

    // Loopback, reti private, link-local e multicast non hanno un paese:
    // evitiamo una lookup inutile (e i falsi positivi dei database).
    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6UniqueLocal)
            return true;

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 0
            || bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || bytes[0] >= 224;
    }
}
