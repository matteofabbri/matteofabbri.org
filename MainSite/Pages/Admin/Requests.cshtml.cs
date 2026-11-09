using System.Globalization;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MainSite.Pages.Admin;

// The requests with the two detection flags, which can be corrected by hand.
// The page sits under /Admin, so it is covered by the blog policy.
//
// Each flag is one button: a click moves it to its next state ("—" → yes → no
// → "—") and saves the row with BotModel="manual", so the backfill, which
// skips rows that have both flags set, never overwrites a manual choice. With
// both flags back on "—" the row is the model's again.
//
// Country, IP, Client ID and User-Agent link to the public requests page with
// the matching filter applied, exactly like the cells of that page.
public sealed class RequestsModel(
    RequestLogStore store,
    IOptions<RequestLogOptions> options,
    ILogger<RequestsModel> logger) : PageModel
{
    private const int PageSize = 200;
    private static readonly int[] AllowedDays = [1, 7, 30, 90];
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<AdminRequestRow> Rows { get; private set; } = [];
    public int PageCount { get; private set; } = 1;
    public int Total { get; private set; }
    public bool LoadFailed { get; private set; }
    public bool LogEnabled => options.Value.Enabled;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Normalize();
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostUpdateAsync(
        string id,
        bool? isBot,
        bool? isMalicious,
        string? toggle,
        int days,
        int pageNumber,
        CancellationToken ct)
    {
        Days = AllowedDays.Contains(days) ? days : 1;
        PageNumber = Math.Max(pageNumber, 1);

        if (string.IsNullOrWhiteSpace(id))
        {
            TempData["Errore"] = "Request id is missing.";
            return RedirectToPage(new { days = Days, pageNumber = PageNumber });
        }

        // One button per flag: the click moves that flag forward and keeps the
        // other one as the form posted it.
        bool? bot = isBot;
        bool? malicious = isMalicious;
        if (string.Equals(toggle, "bot", StringComparison.Ordinal))
            bot = RequestFlagState.Next(isBot);
        else if (string.Equals(toggle, "malicious", StringComparison.Ordinal))
            malicious = RequestFlagState.Next(isMalicious);

        try
        {
            var entry = await store.GetAsync(id, ct);
            if (entry is null)
            {
                TempData["Errore"] = "Request not found.";
                return RedirectToPage(new { days = Days, pageNumber = PageNumber });
            }

            var updated = entry with
            {
                IsBot = bot,
                IsMaliciousURL = malicious,
                BotProbability = null,
                BotModel = bot is null && malicious is null ? null : "manual",
            };

            bool saved = await store.UpdateAsync(updated, ct);
            TempData[saved ? "Messaggio" : "Errore"] =
                saved ? "Flags updated." : "Could not update the request.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Requests admin: update failed");
            TempData["Errore"] = "Could not update the request.";
        }

        return RedirectToPage(new { days = Days, pageNumber = PageNumber });
    }

    public string FormatWhen(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Rome).ToString("dd/MM/yyyy HH:mm:ss", English);

    public string FormatDuration(double milliseconds) => milliseconds.ToString("N1", English) + " ms";

    public string FormatNumber(int value) => value.ToString("N0", English);

    public Dictionary<string, string> PageRoute(int page) => new()
    {
        ["days"] = Days.ToString(CultureInfo.InvariantCulture),
        ["pageNumber"] = Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
    };

    // Links to the public requests page, whose cells filter on the same
    // parameters (country, ip, clientId, userAgent).
    public Dictionary<string, string> FilterRoute(string key, string value) => new()
    {
        ["days"] = Days.ToString(CultureInfo.InvariantCulture),
        ["pageNumber"] = "1",
        [key] = value,
    };

    private void Normalize()
    {
        Days = AllowedDays.Contains(Days) ? Days : 1;
        PageNumber = Math.Max(PageNumber, 1);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            DateTimeOffset to = DateTimeOffset.UtcNow;
            DateTimeOffset from = to.AddDays(-Days);
            var page = await store.GetPageAsync(PageNumber, PageSize, from, ct);

            Total = page.Total;
            PageCount = page.PageCount;
            PageNumber = page.Page;
            Rows = page.Entries.Select(ToRow).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Requests admin: could not read from Sperone");
            LoadFailed = true;
        }
    }

    private static AdminRequestRow ToRow(RequestLogEntry entry)
    {
        string path = entry.Path + entry.QueryString;
        return new AdminRequestRow(
            entry.Id,
            entry.Timestamp,
            entry.Method,
            path,
            entry.StatusCode,
            CountryCodeOf(entry.CountryCode),
            WorldCountries.NameOf(entry.CountryCode),
            entry.RemoteIp ?? "—",
            string.IsNullOrEmpty(entry.ClientId) ? "—" : entry.ClientId,
            entry.UserAgent ?? "—",
            entry.DurationMs,
            entry.IsBot,
            entry.IsMaliciousURL,
            entry.BotModel,
            RequestLinks.SafeHref(path));
    }

    private static string CountryCodeOf(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "ZZ" : code.Trim().ToUpperInvariant();
}

public sealed record AdminRequestRow(
    string Id,
    DateTimeOffset When,
    string Method,
    string Path,
    int StatusCode,
    string CountryCode,
    string Country,
    string RemoteIp,
    string ClientId,
    string UserAgent,
    double DurationMs,
    bool? IsBot,
    bool? IsMaliciousURL,
    string? BotModel,
    string? Href);
