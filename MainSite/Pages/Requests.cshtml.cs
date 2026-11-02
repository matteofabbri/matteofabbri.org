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
// calcolato da IP + user-agent. Un grafico a barre conta i visitatori unici
// (client distinti) di ogni giorno.
public sealed class RequestsModel(
    IServiceProvider services,
    IOptions<RequestLogOptions> options,
    IMemoryCache cache,
    ILogger<RequestsModel> logger) : PageModel
{
    private static readonly int[] AllowedDays = [1, 7, 30, 90];
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public const int PageSize = 200;

    public int Days { get; private set; } = 1;
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
    public IReadOnlyList<VisitorBucket> VisitorBuckets { get; private set; } = [];
    public int UniqueVisitors { get; private set; }

    // Le ultime 24 ore hanno barre orarie; i periodi più lunghi barre giornaliere.
    public bool HourlyPeriod => Days == 1;

    public string PeriodLabel => Days switch
    {
        1 => "last 24 hours",
        7 => "last 7 days",
        90 => "last 90 days",
        _ => "last 30 days",
    };

    public string ChartTitle => HourlyPeriod ? "Unique visitors per hour" : "Unique visitors per day";

    public async Task OnGetAsync(
        int? days,
        int? pageNumber,
        string? country,
        string? ip,
        string? clientId,
        string? userAgent,
        CancellationToken ct)
    {
        Days = AllowedDays.Contains(days ?? 0) ? days!.Value : 1;
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
            DateTimeOffset to = DateTimeOffset.UtcNow;
            DateTimeOffset from = to.AddDays(-Days);

            if (HasFilters)
            {
                string entriesKey = $"requests:public:{Days}";
                if (!cache.TryGetValue(entriesKey, out IReadOnlyList<RequestLogEntry>? entries) || entries is null)
                {
                    entries = await store.GetPublicEntriesAsync(from, ct);
                    cache.Set(entriesKey, entries, TimeSpan.FromSeconds(20));
                }

                var filtered = ApplyFiltered(entries, requestedPage);
                SetVisitors(filtered, from, to);
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

            // I visitatori unici vengono dalle righe pubbliche, che non c'erano
            // nella lettura solo-indice dei conteggi: stessa cache dei filtri.
            string visitorsKey = $"requests:public:{Days}";
            if (!cache.TryGetValue(visitorsKey, out IReadOnlyList<RequestLogEntry>? publicEntries) || publicEntries is null)
            {
                publicEntries = await store.GetPublicEntriesAsync(from, ct);
                cache.Set(visitorsKey, publicEntries, TimeSpan.FromSeconds(20));
            }

            SetVisitors(publicEntries, from, to);

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

    private IReadOnlyList<RequestLogEntry> ApplyFiltered(IReadOnlyList<RequestLogEntry> entries, int requestedPage)
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
        return filtered;
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

    // Visitatori unici: client distinti per bucket. Nelle ultime 24 ore il
    // bucket è un'ora (barre allineate all'ora UTC, etichette in ora di Roma);
    // nei periodi più lunghi è un giorno di Roma, compresi quelli senza
    // traffico. Le righe senza id cliente (nessun IP e nessun user-agent)
    // restano fuori perché non identificano nessuno.
    private void SetVisitors(IReadOnlyList<RequestLogEntry> entries, DateTimeOffset from, DateTimeOffset to)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (HourlyPeriod)
        {
            DateTimeOffset first = HourStart(from);
            int count = Math.Max(1, (int)(HourStart(to).AddHours(1) - first).TotalHours);
            var sets = NewVisitorSets(count);

            foreach (var entry in entries)
            {
                string clientId = ClientIdOf(entry);
                if (string.IsNullOrEmpty(clientId))
                    continue;

                int index = (int)(entry.Timestamp - first).TotalHours;
                if (index < 0 || index >= count)
                    continue;

                sets[index].Add(clientId);
                known.Add(clientId);
            }

            VisitorBuckets = Enumerable.Range(0, count)
                .Select(i => new VisitorBucket(first.AddHours(i), TimeSpan.FromHours(1), sets[i].Count))
                .ToList();
        }
        else
        {
            DateOnly firstDay = RomeDay(from);
            DateOnly lastDay = RomeDay(to);
            int count = lastDay.DayNumber - firstDay.DayNumber + 1;
            var sets = NewVisitorSets(count);

            foreach (var entry in entries)
            {
                string clientId = ClientIdOf(entry);
                if (string.IsNullOrEmpty(clientId))
                    continue;

                int index = RomeDay(entry.Timestamp).DayNumber - firstDay.DayNumber;
                if (index < 0 || index >= count)
                    continue;

                sets[index].Add(clientId);
                known.Add(clientId);
            }

            var buckets = new List<VisitorBucket>(count);
            for (int i = 0; i < count; i++)
                buckets.Add(new VisitorBucket(RomeMidnight(firstDay.AddDays(i)), TimeSpan.FromDays(1), sets[i].Count));

            VisitorBuckets = buckets;
        }

        UniqueVisitors = known.Count;
    }

    private static HashSet<string>[] NewVisitorSets(int count)
    {
        var sets = new HashSet<string>[count];
        for (int i = 0; i < count; i++)
            sets[i] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return sets;
    }

    private static DateTimeOffset HourStart(DateTimeOffset value) =>
        new(value.UtcDateTime.Ticks - value.UtcDateTime.Ticks % TimeSpan.TicksPerHour, TimeSpan.Zero);

    private static DateTimeOffset RomeMidnight(DateOnly day)
    {
        DateTime local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Rome.GetUtcOffset(local));
    }

    private static DateTimeOffset RomeTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Rome);

    // Geometria del grafico: coordinate già formattate con il punto decimale,
    // perché una cultura diversa da en scriverebbe la virgola nell'SVG.
    public IReadOnlyList<VisitorChartBar> ChartBars()
    {
        if (VisitorBuckets.Count == 0)
            return [];

        const double width = 720;
        const double plotHeight = 150;
        const double baseline = 170;

        int max = VisitorBuckets.Max(bucket => bucket.Visitors);
        double slot = width / VisitorBuckets.Count;
        double barWidth = Math.Max(1.5, slot - Math.Min(5, slot * 0.28));
        int tickStep = VisitorBuckets.Count <= 14
            ? 1
            : HourlyPeriod ? 3 : VisitorBuckets.Count <= 40 ? 5 : 15;

        var bars = new List<VisitorChartBar>(VisitorBuckets.Count);
        for (int i = 0; i < VisitorBuckets.Count; i++)
        {
            VisitorBucket bucket = VisitorBuckets[i];
            double height = max > 0 && bucket.Visitors > 0
                ? Math.Max(2, bucket.Visitors * plotHeight / max)
                : 0;

            string title = HourlyPeriod
                ? $"{RomeTime(bucket.Start).ToString("HH:mm", English)}–{RomeTime(bucket.Start.AddHours(1)).ToString("HH:mm", English)} on {RomeTime(bucket.Start).ToString("dddd d MMMM yyyy", English)}: {FormatNumber(bucket.Visitors)} unique visitors"
                : $"{RomeTime(bucket.Start).ToString("dddd d MMMM yyyy", English)}: {FormatNumber(bucket.Visitors)} unique visitors";

            bars.Add(new VisitorChartBar(
                Num(i * slot + (slot - barWidth) / 2),
                Num(barWidth),
                Num(baseline - height),
                Num(height),
                title,
                i % tickStep == 0
                    ? RomeTime(bucket.Start).ToString(HourlyPeriod ? "HH:mm" : "d MMM", English)
                    : null,
                Num(i * slot + slot / 2)));
        }

        return bars;
    }

    public string ChartMax() => VisitorBuckets.Count == 0
        ? "0"
        : FormatNumber(VisitorBuckets.Max(bucket => bucket.Visitors));

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static DateOnly RomeDay(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, Rome).DateTime);

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
        VisitorBuckets = [];
        UniqueVisitors = 0;
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

// Un bucket del grafico dei visitatori — un'ora per le ultime 24 ore, un
// giorno di Roma per i periodi più lunghi — con il suo inizio (in UTC) e il
// numero di client distinti che ci sono comparsi.
public sealed record VisitorBucket(DateTimeOffset Start, TimeSpan Duration, int Visitors);

public sealed record VisitorChartBar(
    string X,
    string Width,
    string Y,
    string Height,
    string Title,
    string? TickLabel,
    string TickX);

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
