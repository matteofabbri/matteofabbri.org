using MainSite.Blog;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages;

// Pagina 404: pesca a caso un media caricato da /admin/404 (nel volume del
// blog). Se non ce ne sono, usa le immagini/video committati in wwwroot/404.
public sealed class NotFoundModel(NotFoundMediaStore uploaded, IWebHostEnvironment environment) : PageModel
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".m4v",
    };

    public string? MediaUrl { get; private set; }
    public string? MediaName { get; private set; }
    public bool IsVideo { get; private set; }

    public void OnGet()
    {
        var media = uploaded.PickRandom();
        if (media is not null)
        {
            MediaUrl = media.Url;
            MediaName = media.FileName;
            IsVideo = media.IsVideo;
            return;
        }

        string webRoot = environment.WebRootPath
            ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        string directory = Path.Combine(webRoot, "404");
        if (!Directory.Exists(directory))
            return;

        string[] files = Directory.EnumerateFiles(directory)
            .Where(file =>
            {
                string extension = Path.GetExtension(file);
                return ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension);
            })
            .ToArray();

        if (files.Length == 0)
            return;

        string file = files[Random.Shared.Next(files.Length)];
        string name = Path.GetFileName(file);
        MediaName = name;
        MediaUrl = "/404/" + Uri.EscapeDataString(name);
        IsVideo = VideoExtensions.Contains(Path.GetExtension(name));
    }
}
