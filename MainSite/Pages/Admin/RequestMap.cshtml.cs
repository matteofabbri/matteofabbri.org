using System.Globalization;
using System.Text.Json;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MainSite.Pages.Admin;

// Mappa delle richieste per regione. I conteggi vengono dagli indici Sperone
// (scalar region + timestamp): non si leggono i payload e non si mostrano IP.
public sealed class RequestMapModel(
    IServiceProvider services,
    IOptions<RequestLogOptions> options,
    ILogger<RequestMapModel> logger) : PageModel
{
    private static readonly int[] AllowedDays = [7, 30, 90];

    public int Days { get; private set; } = 30;
    public bool LogEnabled => options.Value.Enabled;
    public bool GeoIpAvailable { get; private set; }
    public bool LoadFailed { get; private set; }
    public string GeoIpPath { get; private set; } = "";
    public DateTimeOffset? GeoIpUpdated { get; private set; }

    public int TotalRequests { get; private set; }
    public int GeocodedRequests { get; private set; }
    public int UnknownRequests => Math.Max(0, TotalRequests - GeocodedRequests);

    // Ordinate per numero di richieste: la tabella resta utile anche senza mappa.
    public IReadOnlyList<RegionCount> Regions { get; private set; } = [];

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

        var resolver = services.GetService<RequestRegionResolver>();
        GeoIpAvailable = resolver?.Available ?? false;
        GeoIpPath = resolver?.ResolvedDatabasePath ?? options.Value.EffectiveGeoIpDatabasePath;
        GeoIpUpdated = resolver?.LastUpdatedUtc;

        var store = services.GetService<RequestLogStore>();
        if (store is null)
        {
            Regions = BuildRows(new Dictionary<int, int>());
            CountsJson = JsonSerializer.Serialize(Regions.ToDictionary(r => r.Code, r => r.Count));
            return;
        }

        try
        {
            DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-Days);
            TotalRequests = await store.CountSinceAsync(from, ct);

            var counts = new Dictionary<int, int>(ItalianRegions.All.Count);
            foreach (var region in ItalianRegions.All)
                counts[region.Code] = await store.CountByRegionAsync(region.Code, from, ct);

            GeocodedRequests = counts.Values.Sum();
            Regions = BuildRows(counts);
            CountsJson = JsonSerializer.Serialize(counts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Mappa richieste: impossibile leggere i conteggi da Sperone");
            LoadFailed = true;
            TotalRequests = 0;
            GeocodedRequests = 0;
            Regions = BuildRows(new Dictionary<int, int>());
            CountsJson = JsonSerializer.Serialize(Regions.ToDictionary(r => r.Code, r => r.Count));
        }
    }

    public string PercentLabel(int count) => TotalRequests == 0
        ? "0,0"
        : (count * 100.0 / TotalRequests).ToString("0.0", CultureInfo.GetCultureInfo("it-IT"));

    public string FormatNumber(int value) => value.ToString("N0", CultureInfo.GetCultureInfo("it-IT"));

    public string BarWidth(int count) => TotalRequests == 0
        ? "0"
        : (count * 100.0 / TotalRequests).ToString("0.0", CultureInfo.InvariantCulture);

    private static List<RegionCount> BuildRows(IReadOnlyDictionary<int, int> counts) =>
        ItalianRegions.All
            .Select(region => new RegionCount(
                region.Code,
                region.Name,
                counts.TryGetValue(region.Code, out int count) ? count : 0))
            .OrderByDescending(region => region.Count)
            .ThenBy(region => region.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed record RegionCount(int Code, string Name, int Count);
