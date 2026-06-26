using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

// Upload e gestione delle foto/video della pagina 404. Stanno nel volume del
// blog, in 404/, e vengono scelti a caso quando la pagina 404 si apre.
public sealed class NotFoundMediaModel(NotFoundMediaStore media, BlogContentStore content) : PageModel
{
    public IReadOnlyList<MediaFile> Files { get; private set; } = [];

    // Il deposito dei download: da qui si copia dentro la rotazione della 404.
    public IReadOnlyList<ContentFile> ContentFiles { get; private set; } = [];

    public long ContentBytes => ContentFiles.Sum(file => file.Size);

    public string ContentSizeLabel => MediaFile.FormatSize(ContentBytes);

    [BindProperty]
    public IFormFile? Upload { get; set; }

    public void OnGet()
    {
        Files = media.List();
        ContentFiles = content.List();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Upload is null || Upload.Length == 0)
        {
            TempData["Errore"] = "Choose a photo or video to upload.";
            return RedirectToPage();
        }

        try
        {
            await using var stream = Upload.OpenReadStream();
            var saved = await media.SaveAsync(Upload.FileName, stream, Upload.Length, ct);
            TempData["Messaggio"] = $"Uploaded: {saved.FileName}";
        }
        catch (BlogValidationException ex)
        {
            TempData["Errore"] = ex.Message;
        }

        return RedirectToPage();
    }

    // Copia tutti i file della cartella content dentro 404. Quelli già presenti
    // e i formati non mostrabili vengono saltati e riportati.
    public async Task<IActionResult> OnPostCopyFromContentAsync(CancellationToken ct)
    {
        var result = await content.CopyToAsync(media, ct);

        TempData["Messaggio"] = result.Copied > 0
            ? $"Copied {result.Copied} file(s) from content into the 404 rotation."
            : "Nothing copied from content.";

        if (result.Skipped.Count > 0)
            TempData["Errore"] = "Skipped: " + string.Join("; ", result.Skipped);

        return RedirectToPage();
    }

    public IActionResult OnPostDelete(string fileName)
    {
        bool deleted = media.Delete(fileName);
        TempData[deleted ? "Messaggio" : "Errore"] =
            deleted ? "Media deleted." : "Media not found.";

        return RedirectToPage();
    }
}
