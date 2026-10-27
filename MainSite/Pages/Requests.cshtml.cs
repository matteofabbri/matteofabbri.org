using System.Globalization;
using System.Text.Json;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MainSite.Pages;

// Mappa e lista delle richieste del sito. La pagina è pubblica e aggregata per
// paese: non mostra né IP né utenti autenticati. I conteggi vengono dagli
// indici Sperone (scalar country + timestamp); la lista legge solo le ultime
// righe e lascia fuori le pagine /admin (possono contenere slug di bozze).
public sealed class RequestsModel(
    IServiceProvider services,
    IOptions<RequestLogOptions> options,
    IMemoryCache cache,
    ILogger<RequestsModel> logger) : PageModel
{
    private static readonly int[] AllowedDays = [7, 30, 90];
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private const int RecentLimit = 50;

    public int Days { get; private set; } = 30;
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

    public async Task OnGetAsync(int? days, CancellationToken ct)
    {
        Days = AllowedDays.Contains(days ?? 0) ? days!.Value : 30;

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
            string cacheKey = $"richieste:paesi:{Days}";
            if (!cache.TryGetValue(cacheKey, out PageData? data) || data is null)
            {
                data = await LoadAsync(store, ct);
                cache.Set(cacheKey, data, TimeSpan.FromSeconds(20));
            }

            TotalRequests = data.Total;
            GeocodedRequests = data.Geocoded;
            Countries = data.Counts
                .Select(pair => new CountryCount(pair.Key, WorldCountries.NameOf(pair.Key), pair.Value))
                .OrderByDescending(country => country.Count)
                .ThenBy(country => country.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            CountsJson = JsonSerializer.Serialize(data.Counts);
            Recent = data.Recent
                .Select(entry => new RequestRow(
                    entry.Timestamp,
                    entry.Method,
                    entry.Path + entry.QueryString,
                    entry.StatusCode,
                    WorldCountries.NameOf(entry.CountryCode),
                    entry.Client ?? "Sconosciuto",
                    entry.DeviceType,
                    entry.DurationMs))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Richieste: impossibile leggere i conteggi da Sperone");
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

    public string DeviceLabel(string? device) => device switch
    {
        "desktop" => "Desktop",
        "mobile" => "Mobile",
        "tablet" => "Tablet",
        "bot" => "Bot",
        _ => "Sconosciuto",
    };

    private async Task<PageData> LoadAsync(RequestLogStore store, CancellationToken ct)
    {
        DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-Days);
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

        var recent = await store.GetRecentAsync(RecentLimit, from, ct);
        return new PageData(total, geocoded, counts, recent);
    }

    private void Empty()
    {
        TotalRequests = 0;
        GeocodedRequests = 0;
        Countries = [];
        Recent = [];
        CountsJson = "{}";
    }

    private sealed record PageData(
        int Total,
        int Geocoded,
        IReadOnlyDictionary<string, int> Counts,
        IReadOnlyList<RequestLogEntry> Recent);
}

public sealed record CountryCount(string Code, string Name, int Count);

public sealed record RequestRow(
    DateTimeOffset When,
    string Method,
    string Path,
    int StatusCode,
    string Country,
    string Client,
    string? DeviceType,
    double DurationMs);
