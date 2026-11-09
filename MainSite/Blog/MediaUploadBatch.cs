using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace MainSite.Blog;

// Multiple-file upload for the admin media pages: one invalid file does not
// stop the others, and the page gets a single short summary instead of a
// redirect per file.
public static class MediaUploadBatch
{
    // Ceiling for the whole request: a single video can reach 25 MB, so this
    // covers a handful of them while staying an explicit limit.
    public const long MaxRequestBytes = 200L * 1024 * 1024;

    public sealed record Result(IReadOnlyList<string> Saved, IReadOnlyList<string> Failed);

    public static async Task<Result> SaveAsync(
        IEnumerable<IFormFile> files,
        Func<string, Stream, long, CancellationToken, Task<string>> save,
        CancellationToken ct)
    {
        var saved = new List<string>();
        var failed = new List<string>();

        foreach (IFormFile file in files)
        {
            if (file.Length <= 0)
            {
                failed.Add($"{file.FileName}: the file is empty.");
                continue;
            }

            try
            {
                await using Stream stream = file.OpenReadStream();
                saved.Add(await save(file.FileName, stream, file.Length, ct));
            }
            catch (BlogValidationException ex)
            {
                failed.Add($"{file.FileName}: {ex.Message}");
            }
        }

        return new Result(saved, failed);
    }

    // TempData travels in a cookie, so the summary stays short on purpose.
    public static void Report(ITempDataDictionary tempData, Result result)
    {
        if (result.Saved.Count > 0)
        {
            tempData["Messaggio"] = result.Saved.Count == 1
                ? $"Uploaded: {result.Saved[0]}"
                : $"Uploaded {result.Saved.Count} files.";
        }

        if (result.Failed.Count == 0)
            return;

        var shown = result.Failed.Take(3).ToList();
        if (result.Failed.Count > shown.Count)
            shown.Add($"and {result.Failed.Count - shown.Count} more.");

        tempData["Errore"] = string.Join(" ", shown);
    }
}
