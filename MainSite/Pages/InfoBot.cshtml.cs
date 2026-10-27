using System.Globalization;
using System.Text;
using MainSite.Blog;
using MainSite.InfoBots;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages;

// Payload ricevuti da POST /info-send. Dati grezzi (spesso robaccia inviata da
// bot): visibili solo agli autori autenticati.
[Authorize(Policy = BlogAuth.Policy)]
public sealed class InfoBotModel(RequestLogStore store, ILogger<InfoBotModel> logger) : PageModel
{
    public const int PageSize = 50;

    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public IReadOnlyList<InfoEntry> Entries { get; private set; } = [];
    public int PageNumber { get; private set; } = 1;
    public int PageCount { get; private set; } = 1;
    public int Total { get; private set; }
    public bool LoadFailed { get; private set; }

    public int FirstOnPage => Total == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
    public int LastOnPage => Math.Min(PageNumber * PageSize, Total);

    public async Task OnGetAsync(int? pageNumber, CancellationToken ct)
    {
        try
        {
            var page = await store.GetInfoPageAsync(
                pageNumber is > 0 ? pageNumber.Value : 1,
                PageSize,
                ct);

            Entries = page.Entries;
            PageNumber = page.Page;
            PageCount = page.PageCount;
            Total = page.Total;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "info-bot: impossibile leggere i payload da Sperone");
            LoadFailed = true;
        }
    }

    public string CountryLabel(InfoEntry entry) =>
        string.IsNullOrWhiteSpace(entry.CountryCode)
            ? "Unknown"
            : entry.CountryName ?? WorldCountries.NameOf(entry.CountryCode);

    public string FormatWhen(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Rome).ToString("dd/MM/yyyy HH:mm:ss", English);

    public string FormatNumber(int value) => value.ToString("N0", English);

    public string FormatSize(InfoEntry entry) =>
        Encoding.UTF8.GetByteCount(entry.Payload).ToString("N0", English) + " B";
}
