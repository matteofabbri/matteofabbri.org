using System.Globalization;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

// Gestione delle frasi del jumbotron. La pagina sta sotto /Admin, quindi è
// protetta dalla policy del blog: entra qualunque utente registrato su mailcow.
public sealed class LedesModel(RequestLogStore store, ILogger<LedesModel> logger) : PageModel
{
    private const int MaxLength = 300;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public IReadOnlyList<LedeEntry> Ledes { get; private set; } = [];
    public bool LoadFailed { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Ledes = await store.GetLedesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ledes: impossibile leggere da Sperone");
            LoadFailed = true;
        }
    }

    public async Task<IActionResult> OnPostAddAsync(string? newText, CancellationToken ct)
    {
        if (!TryClean(newText, out string text, out string? error))
        {
            TempData["Errore"] = error;
            return RedirectToPage();
        }

        try
        {
            var entry = await store.AddLedeAsync(text, ct);
            TempData[entry is null ? "Errore" : "Messaggio"] =
                entry is null ? "Could not save the lede." : "Lede added.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ledes: aggiunta fallita");
            TempData["Errore"] = "Could not save the lede.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(string id, string? text, CancellationToken ct)
    {
        if (!TryClean(text, out string cleaned, out string? error))
        {
            TempData["Errore"] = error;
            return RedirectToPage();
        }

        try
        {
            var entry = await store.UpdateLedeAsync(id, cleaned, ct);
            TempData[entry is null ? "Errore" : "Messaggio"] =
                entry is null ? "Lede not found." : "Lede updated.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ledes: modifica fallita");
            TempData["Errore"] = "Could not update the lede.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, CancellationToken ct)
    {
        try
        {
            bool deleted = await store.DeleteLedeAsync(id, ct);
            TempData[deleted ? "Messaggio" : "Errore"] =
                deleted ? "Lede deleted." : "Lede not found.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ledes: eliminazione fallita");
            TempData["Errore"] = "Could not delete the lede.";
        }

        return RedirectToPage();
    }

    public string FormatWhen(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"))
            .ToString("dd/MM/yyyy HH:mm", English);

    private static bool TryClean(string? raw, out string text, out string? error)
    {
        text = (raw ?? "").Trim();
        error = null;

        if (text.Length == 0)
            error = "Lede text is required.";
        else if (text.Length > MaxLength)
            error = $"Lede cannot exceed {MaxLength} characters.";

        return error is null;
    }
}
