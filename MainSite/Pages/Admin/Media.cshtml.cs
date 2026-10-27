using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

public sealed class MediaModel(BlogMediaStore media) : PageModel
{
    public IReadOnlyList<BlogMediaFile> Files { get; private set; } = [];

    [BindProperty]
    public IFormFile? Upload { get; set; }

    public void OnGet() => Files = media.List();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Upload is null || Upload.Length == 0)
        {
            TempData["Errore"] = "Choose a file to upload.";
            return RedirectToPage();
        }

        try
        {
            await using var stream = Upload.OpenReadStream();
            var saved = await media.SaveAsync(Upload.FileName, stream, Upload.Length, ct);
            TempData["Messaggio"] = $"Image uploaded: {saved.FileName}";
        }
        catch (BlogValidationException ex)
        {
            TempData["Errore"] = ex.Message;
        }

        return RedirectToPage();
    }
}
