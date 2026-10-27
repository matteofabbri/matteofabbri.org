using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

// Upload e gestione delle foto/video della pagina 404. Stanno nel volume del
// blog, in 404/, e vengono scelti a caso quando la pagina 404 si apre.
public sealed class NotFoundMediaModel(NotFoundMediaStore media) : PageModel
{
    public IReadOnlyList<MediaFile> Files { get; private set; } = [];

    [BindProperty]
    public IFormFile? Upload { get; set; }

    public void OnGet() => Files = media.List();

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

    public IActionResult OnPostDelete(string fileName)
    {
        bool deleted = media.Delete(fileName);
        TempData[deleted ? "Messaggio" : "Errore"] =
            deleted ? "Media deleted." : "Media not found.";

        return RedirectToPage();
    }
}
