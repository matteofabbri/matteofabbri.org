using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

// Upload and management of the jumbotron photos/videos. They live in the blog
// volume, under jumbotron/, and the home page picks one at random.
[RequestSizeLimit(MediaUploadBatch.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MediaUploadBatch.MaxRequestBytes)]
public sealed class JumbotronModel(JumbotronMediaStore media) : PageModel
{
    public IReadOnlyList<MediaFile> Files { get; private set; } = [];

    [BindProperty]
    public List<IFormFile> Upload { get; set; } = [];

    public void OnGet() => Files = media.List();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Upload is null || Upload.Count == 0)
        {
            TempData["Errore"] = "Choose one or more photos or videos to upload.";
            return RedirectToPage();
        }

        var result = await MediaUploadBatch.SaveAsync(
            Upload,
            async (fileName, stream, length, token) =>
                (await media.SaveAsync(fileName, stream, length, token)).FileName,
            ct);

        MediaUploadBatch.Report(TempData, result);
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
