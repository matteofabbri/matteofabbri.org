using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

[RequestSizeLimit(MediaUploadBatch.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MediaUploadBatch.MaxRequestBytes)]
public sealed class MediaModel(BlogMediaStore media) : PageModel
{
    public IReadOnlyList<BlogMediaFile> Files { get; private set; } = [];

    [BindProperty]
    public List<IFormFile> Upload { get; set; } = [];

    public void OnGet() => Files = media.List();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Upload is null || Upload.Count == 0)
        {
            TempData["Errore"] = "Choose one or more images to upload.";
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
}
