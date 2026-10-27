using System.Globalization;
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
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public const int PageSize = 200;

    public int Days { get; private set; } = 30;
    public int PageNumber { get; private set; } = 1;
    public int PageCount { get; private set; } = 1;
    public int PublicTotal { get; private set; }
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
        7 => "ultimi 7 giorni",
        90 => "ultimi 90 giorni",
        _ => "ultimi 30 giorni",
    };

    public async Task OnGetAsync(int? days, int? pageNumber, CancellationToken ct)
    {
        Days = AllowedDays.Contains(days ?? 0) ? days!.Value : 30;
        int requestedPage = pageNumber is > 0 ? pageNumber.Value : 1;

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

    public string PercentLabel(int count) => TotalRequests == 0
        ? "0,0"
        : (count * 100.0 / TotalRequests).ToString("0.0", Italian);

    public string FormatNumber(int value) => value.ToString("N0", Italian);

    public string BarWidth(int count) => TotalRequests == 0
        ? "0"
        : (count * 100.0 / TotalRequests).ToString("0.0", CultureInfo.InvariantCulture);

    public string FormatWhen(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Rome).ToString("dd/MM/yyyy HH:mm:ss", Italian);

    public string FormatDuration(double milliseconds) => milliseconds.ToString("N1", Italian) + " ms";

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

    private static RequestRow ToRow(RequestLogEntry entry) => new(
        entry.Timestamp,
        entry.Method,
        entry.Path + entry.QueryString,
        entry.StatusCode,
        WorldCountries.NameOf(entry.CountryCode),
        entry.RemoteIp ?? "—",
        entry.ClientId ?? ClientIds.For(entry.RemoteIp, entry.UserAgent) ?? "—",
        entry.UserAgent ?? "—",
        entry.DurationMs);

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
    string Country,
    string RemoteIp,
    string ClientId,
    string UserAgent,
    double DurationMs);
