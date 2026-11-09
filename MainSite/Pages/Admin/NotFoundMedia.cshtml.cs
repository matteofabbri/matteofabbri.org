using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

// Upload and management of the 404 page photos/videos. They live in the blog
// volume, under 404/, and the 404 page picks one at random when it opens.
[RequestSizeLimit(MediaUploadBatch.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MediaUploadBatch.MaxRequestBytes)]
public sealed class NotFoundMediaModel(NotFoundMediaStore media, BlogContentStore content) : PageModel
{
    public IReadOnlyList<MediaFile> Files { get; private set; } = [];

    // Where the downloads wait: from here they are copied into the 404
    // rotation.
    public IReadOnlyList<ContentFile> ContentFiles { get; private set; } = [];

    public long ContentBytes => ContentFiles.Sum(file => file.Size);

    public string ContentSizeLabel => MediaFile.FormatSize(ContentBytes);

    [BindProperty]
    public List<IFormFile> Upload { get; set; } = [];

    public void OnGet()
    {
        Files = media.List();
        ContentFiles = content.List();
    }

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

    // Copies every file of the content folder into 404. Files already there
    // and formats the browser cannot show are skipped and reported.
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
