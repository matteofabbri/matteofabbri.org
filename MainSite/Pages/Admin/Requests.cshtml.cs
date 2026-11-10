using System.Globalization;
using BotDetection.Client;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MainSite.Pages.Admin;

// The requests with the two detection flags, which can be corrected by hand.
// The page sits under /Admin, so it is covered by the blog policy.
//
// Each flag is one button: a click moves it to its next state ("—" → yes → no
// → "—") and saves it with BotModel="manual", so the backfill, which skips rows
// that have both flags set, never overwrites a manual choice. A click is about
// the CLIENT, not the single row: every public request of the same Client ID in
// the period gets the same flags. The verdict is also sent to the Python model,
// which applies it to the client's next requests and learns it at the next
// retraining run.
//
// Country, IP, Client ID and User-Agent link to the public requests page with
// the matching filter applied, exactly like the cells of that page.
public sealed class RequestsModel(
    RequestLogStore store,
    IServiceProvider services,
    IMemoryCache cache,
    IOptions<RequestLogOptions> options,
    ILogger<RequestsModel> logger) : PageModel
{
    private const int PageSize = 30;
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

            string clientId = ClientIdOf(entry);
            ClientFlagUpdate update;
            if (clientId.Length == 0)
            {
                // No client identity (no IP and no user agent): the row is all
                // this click can mark, and the model has nothing to learn.
                var single = entry with
                {
                    IsBot = bot,
                    IsMaliciousURL = malicious,
                    BotProbability = null,
                    BotModel = bot is null && malicious is null ? null : "manual",
                };
                bool saved = await store.UpdateAsync(single, ct);
                update = new ClientFlagUpdate(
                    1, saved ? 1 : 0, saved ? 0 : 1, saved ? null : "the store rejected the update");
            }
            else
            {
                IReadOnlyList<RequestLogEntry> entries = await PeriodEntriesAsync(ct);
                update = await store.UpdateClientFlagsAsync(entries, clientId, bot, malicious, ct);
            }

            if (update.Matched == 0)
            {
                TempData["Errore"] = "No request of this client matched in the period.";
                return RedirectToPage(new { days = Days, pageNumber = PageNumber });
            }

            if (update.Updated == 0)
            {
                TempData["Errore"] = $"Could not update the request ({update.FirstError}).";
                return RedirectToPage(new { days = Days, pageNumber = PageNumber });
            }

            // The public page shares this list through the 20-second cache: drop
            // it so the new flags are visible on the next visit, not after the TTL.
            cache.Remove($"requests:public:{Days}");

            string modelNote = clientId.Length == 0
                ? ""
                : await SendClientFeedbackAsync(clientId, bot, malicious, ct);

            TempData["Messaggio"] =
                $"Flags updated on {FormatNumber(update.Updated)} requests of this client ({PeriodLabel})."
                + (update.Skipped > 0 ? $" {FormatNumber(update.Skipped)} could not be updated." : "")
                + modelNote;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Requests admin: update failed");
            TempData["Errore"] = "Could not update the request.";
        }

        return RedirectToPage(new { days = Days, pageNumber = PageNumber });
    }

    private string PeriodLabel => Days switch
    {
        7 => "last 7 days",
        30 => "last 30 days",
        90 => "last 90 days",
        _ => "last 24 hours",
    };

    // The period's public rows, read fresh: a click must include the client's
    // newest requests too, so it does not reuse the public page's cached list.
    // That list is dropped after the update, so the public page does not keep
    // showing the old flags for the rest of the TTL.
    private Task<IReadOnlyList<RequestLogEntry>> PeriodEntriesAsync(CancellationToken ct) =>
        store.GetPublicEntriesAsync(DateTimeOffset.UtcNow.AddDays(-Days), ct);

    // Best effort: the log flags are already saved. The model applies the
    // verdict to the client's next requests and relabels its stored
    // observations, so the next retraining run learns it too.
    private async Task<string> SendClientFeedbackAsync(
        string clientId,
        bool? bot,
        bool? malicious,
        CancellationToken ct)
    {
        var classifier = services.GetService<BotClassifier>();
        if (classifier is null)
            return "";

        try
        {
            var feedback = await classifier.LabelClientAsync(
                clientId, bot, malicious, User.Identity?.Name, ct);

            return feedback switch
            {
                null => " The model did not answer: the flags are saved in the log only.",
                { ManualLabelActive: false } => " The model cleared its manual label for this client.",
                _ => $" The model applied the label to {FormatNumber(feedback.ObservationsUpdated)} observations"
                     + (feedback.RetrainRequested ? " and asked for a retraining run." : "."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The flags are already saved: a model hiccup must not turn a
            // successful update into an error page.
            logger.LogWarning(ex, "Requests admin: the model did not take the client label");
            return " The model did not answer: the flags are saved in the log only.";
        }
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
        string clientId = ClientIdOf(entry);
        return new AdminRequestRow(
            entry.Id,
            entry.Timestamp,
            entry.Method,
            path,
            entry.StatusCode,
            CountryCodeOf(entry.CountryCode),
            WorldCountries.NameOf(entry.CountryCode),
            entry.RemoteIp ?? "—",
            string.IsNullOrEmpty(clientId) ? "—" : clientId,
            entry.UserAgent ?? "—",
            entry.DurationMs,
            entry.IsBot,
            entry.IsMaliciousURL,
            entry.BotModel,
            RequestLinks.SafeHref(path));
    }

    // Same identity the public page shows: the stored id, or the hash of IP and
    // user agent for rows written before the field existed.
    private static string ClientIdOf(RequestLogEntry entry) =>
        entry.ClientId ?? ClientIds.For(entry.RemoteIp, entry.UserAgent) ?? "";

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
