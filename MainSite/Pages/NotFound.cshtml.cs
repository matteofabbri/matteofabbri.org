using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages;

// Pagina 404: pesca a caso un'immagine o un video dalla cartella
// wwwroot/404, così ogni errore ha un contenuto diverso.
public sealed class NotFoundModel(IWebHostEnvironment environment) : PageModel
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
