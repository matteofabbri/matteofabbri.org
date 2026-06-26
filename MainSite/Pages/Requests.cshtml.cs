using System.Globalization;
using System.Net;
using System.Text.Json;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MainSite.Pages;

// Mappa e lista delle richieste del sito. È pubblica e aggregata per paese;
// la lista mostra le richieste pubbliche a pagine da 200, con user-agent e IP
// così come sono stati registrati (nessun parsing) e un id cliente stabile
// calcolato da IP + user-agent.
public sealed class RequestsModel(
    IServiceProvider services,
    IOptions<RequestLogOptions> options,
    IMemoryCache cache,
    ILogger<RequestsModel> logger) : PageModel
{
    private static readonly int[] AllowedDays = [7, 30, 90];
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public const int PageSize = 200;

    public int Days { get; private set; } = 30;
    public int PageNumber { get; private set; } = 1;
    public int PageCount { get; private set; } = 1;
    public int PublicTotal { get; private set; }

    public string? CountryFilter { get; private set; }
    public string? IpFilter { get; private set; }
    public string? ClientIdFilter { get; private set; }
    public string? UserAgentFilter { get; private set; }
    public IReadOnlyList<RequestFilter> ActiveFilters { get; private set; } = [];
    public bool HasFilters => ActiveFilters.Count > 0;
    public int FirstOnPage => PublicTotal == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
    public int LastOnPage => Math.Min(PageNumber * PageSize, PublicTotal);

    public bool LogEnabled => options.Value.Enabled;
    public bool GeoIpAvailable { get; private set; }
    public bool LoadFailed { get; private set; }
    public string GeoIpPath { get; private set; } = "";
    public DateTimeOffset? GeoIpUpdated { get; private set; }

    public int TotalRequests { get; private set; }
    public int GeocodedRequests { get; private set; }
    public int UnknownRequests => Math.Max(0, TotalRequests - GeocodedRequests);

    public IReadOnlyList<CountryCount> Countries { get; private set; } = [];
    public IReadOnlyList<RequestRow> Recent { get; private set; } = [];
    public string CountsJson { get; private set; } = "{}";

    public string PeriodLabel => Days switch
    {
        7 => "last 7 days",
        90 => "last 90 days",
        _ => "last 30 days",
    };

    public async Task OnGetAsync(
        int? days,
        int? pageNumber,
        string? country,
        string? ip,
        string? clientId,
        string? userAgent,
        CancellationToken ct)
    {
        Days = AllowedDays.Contains(days ?? 0) ? days!.Value : 30;
        int requestedPage = pageNumber is > 0 ? pageNumber.Value : 1;

        CountryFilter = NormalizeCountryFilter(country);
        IpFilter = NormalizeIpFilter(ip);
        ClientIdFilter = NormalizeClientIdFilter(clientId);
        UserAgentFilter = NormalizeUserAgentFilter(userAgent);
        ActiveFilters = BuildActiveFilters();

        var resolver = services.GetService<RequestCountryResolver>();
        GeoIpAvailable = resolver?.Available ?? false;
        GeoIpPath = resolver?.ResolvedDatabasePath ?? options.Value.EffectiveGeoIpDatabasePath;
        GeoIpUpdated = resolver?.LastUpdatedUtc;

        var store = services.GetService<RequestLogStore>();
        if (store is null)
        {
            Empty();
            return;
        }

        try
        {
            DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-Days);

            if (HasFilters)
            {
                string entriesKey = $"requests:public:{Days}";
                if (!cache.TryGetValue(entriesKey, out IReadOnlyList<RequestLogEntry>? entries) || entries is null)
                {
                    entries = await store.GetPublicEntriesAsync(from, ct);
                    cache.Set(entriesKey, entries, TimeSpan.FromSeconds(20));
                }

                ApplyFiltered(entries, requestedPage);
                return;
            }

            string countsKey = $"requests:countries:{Days}";
            if (!cache.TryGetValue(countsKey, out CountryStats? stats) || stats is null)
            {
                stats = await LoadCountriesAsync(store, from, ct);
                cache.Set(countsKey, stats, TimeSpan.FromSeconds(20));
            }

            TotalRequests = stats.Total;
            GeocodedRequests = stats.Geocoded;
            Countries = stats.Counts
                .Select(pair => new CountryCount(pair.Key, WorldCountries.NameOf(pair.Key), pair.Value))
                .OrderByDescending(country => country.Count)
                .ThenBy(country => country.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            CountsJson = JsonSerializer.Serialize(stats.Counts);

            string pageKey = $"requests:page:{Days}:{requestedPage}";
            if (!cache.TryGetValue(pageKey, out RequestLogPage? logPage) || logPage is null)
            {
                logPage = await store.GetPageAsync(requestedPage, PageSize, from, ct);
                cache.Set(pageKey, logPage, TimeSpan.FromSeconds(20));
            }

            PageNumber = logPage.Page;
            PageCount = logPage.PageCount;
            PublicTotal = logPage.Total;
            Recent = logPage.Entries.Select(ToRow).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Richieste: impossibile leggere i dati da Sperone");
            LoadFailed = true;
            Empty();
        }
    }

    private void ApplyFiltered(IReadOnlyList<RequestLogEntry> entries, int requestedPage)
    {
        var filtered = entries.Where(Matches).ToList();

        TotalRequests = filtered.Count;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int geocoded = 0;
        foreach (var entry in filtered)
        {
            string code = CountryCodeOf(entry.CountryCode);
            if (code == "ZZ")
                continue;

            geocoded++;
            counts[code] = counts.TryGetValue(code, out int count) ? count + 1 : 1;
        }

        GeocodedRequests = geocoded;
        Countries = counts
            .Select(pair => new CountryCount(pair.Key, WorldCountries.NameOf(pair.Key), pair.Value))
            .OrderByDescending(country => country.Count)
            .ThenBy(country => country.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        CountsJson = JsonSerializer.Serialize(counts);

        PublicTotal = filtered.Count;
        PageCount = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
        PageNumber = Math.Clamp(requestedPage, 1, PageCount);

        int skip = (PageNumber - 1) * PageSize;
        Recent = filtered.Skip(skip).Take(PageSize).Select(ToRow).ToList();
    }

    private bool Matches(RequestLogEntry entry)
    {
        if (CountryFilter is not null && CountryCodeOf(entry.CountryCode) != CountryFilter)
            return false;
        if (IpFilter is not null && !string.Equals(entry.RemoteIp ?? "", IpFilter, StringComparison.Ordinal))
            return false;
        if (ClientIdFilter is not null
            && !string.Equals(ClientIdOf(entry), ClientIdFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (UserAgentFilter is not null
            && !string.Equals(entry.UserAgent ?? "", UserAgentFilter, StringComparison.Ordinal))
            return false;
        return true;
    }

    private IReadOnlyList<RequestFilter> BuildActiveFilters()
    {
        var filters = new List<RequestFilter>(4);
        if (CountryFilter is not null)
            filters.Add(new RequestFilter("country", $"Country: {CountryLabel(CountryFilter)}", CountryFilter));
        if (IpFilter is not null)
            filters.Add(new RequestFilter("ip", $"IP: {IpFilter}", IpFilter));
        if (ClientIdFilter is not null)
            filters.Add(new RequestFilter("clientId", $"Client ID: {ClientIdFilter}", ClientIdFilter));
        if (UserAgentFilter is not null)
            filters.Add(new RequestFilter("userAgent", $"User-Agent: {UserAgentFilter}", UserAgentFilter));
        return filters;
    }

    public Dictionary<string, string> FilterRoute(string key, string value)
    {
        var route = CurrentRoute();
        route[key] = value;
        route["pageNumber"] = "1";
        return route;
    }

    public Dictionary<string, string> RemoveFilterRoute(string key)
    {
        var route = CurrentRoute();
        route.Remove(key);
        route["pageNumber"] = "1";
        return route;
    }

    public Dictionary<string, string> PageRoute(int page)
    {
        var route = CurrentRoute();
        route["pageNumber"] = Math.Max(1, page).ToString(CultureInfo.InvariantCulture);
        return route;
    }

    private Dictionary<string, string> CurrentRoute()
    {
        var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["days"] = Days.ToString(CultureInfo.InvariantCulture),
            ["pageNumber"] = "1",
        };

        if (CountryFilter is not null)
            route["country"] = CountryFilter;
        if (IpFilter is not null)
            route["ip"] = IpFilter;
        if (ClientIdFilter is not null)
            route["clientId"] = ClientIdFilter;
        if (UserAgentFilter is not null)
            route["userAgent"] = UserAgentFilter;

        return route;
    }

    private static string CountryCodeOf(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "ZZ" : code.Trim().ToUpperInvariant();

    private static string CountryLabel(string code) =>
        code == "ZZ" ? "Unknown" : WorldCountries.NameOf(code);

    private static string ClientIdOf(RequestLogEntry entry) =>
        entry.ClientId ?? ClientIds.For(entry.RemoteIp, entry.UserAgent) ?? "";

    private static string? NormalizeCountryFilter(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 8)
            return null;
        foreach (char c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return null;
        return value.ToUpperInvariant();
    }

    private static string? NormalizeIpFilter(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 64 || !IPAddress.TryParse(value, out _))
            return null;
        return value;
    }

    private static string? NormalizeClientIdFilter(string? value)
    {
        value = value?.Trim();
        if (value is not { Length: 16 } || !value.All(Uri.IsHexDigit))
            return null;
        return value.ToLowerInvariant();
    }

    private static string? NormalizeUserAgentFilter(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512)
            return null;
        return value;
    }

    public string PercentLabel(int count) => TotalRequests == 0
        ? "0,0"
        : (count * 100.0 / TotalRequests).ToString("0.0", English);

    public string FormatNumber(int value) => value.ToString("N0", English);

    public string BarWidth(int count) => TotalRequests == 0
        ? "0"
        : (count * 100.0 / TotalRequests).ToString("0.0", CultureInfo.InvariantCulture);

    public string FormatWhen(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Rome).ToString("dd/MM/yyyy HH:mm:ss", English);

    public string FormatDuration(double milliseconds) => milliseconds.ToString("N1", English) + " ms";

    private async Task<CountryStats> LoadCountriesAsync(RequestLogStore store, DateTimeOffset from, CancellationToken ct)
    {
        int total = await store.CountSinceAsync(from, ct);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int geocoded = 0;
        foreach (string code in WorldCountries.Codes)
        {
            int count = await store.CountByCountryAsync(code, from, ct);
            if (count == 0)
                continue;

            counts[code] = count;
            geocoded += count;
        }

        return new CountryStats(total, geocoded, counts);
    }

    private static RequestRow ToRow(RequestLogEntry entry)
    {
        string clientId = ClientIdOf(entry);
        return new RequestRow(
            entry.Timestamp,
            entry.Method,
            entry.Path + entry.QueryString,
            entry.StatusCode,
            CountryCodeOf(entry.CountryCode),
            WorldCountries.NameOf(entry.CountryCode),
            entry.RemoteIp ?? "—",
            string.IsNullOrEmpty(clientId) ? "—" : clientId,
            entry.UserAgent ?? "—",
            entry.DurationMs);
    }

    private void Empty()
    {
        TotalRequests = 0;
        GeocodedRequests = 0;
        Countries = [];
        Recent = [];
        CountsJson = "{}";
        PageNumber = 1;
        PageCount = 1;
        PublicTotal = 0;
    }

    private sealed record CountryStats(
        int Total,
        int Geocoded,
        IReadOnlyDictionary<string, int> Counts);
}

public sealed record CountryCount(string Code, string Name, int Count);

public sealed record RequestRow(
    DateTimeOffset When,
    string Method,
    string Path,
    int StatusCode,
    string CountryCode,
    string Country,
    string RemoteIp,
    string ClientId,
    string UserAgent,
    double DurationMs);

public sealed record RequestFilter(string Key, string Label, string Value);
