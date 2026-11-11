using MainSite.Blog;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Blog;

public sealed class IndexModel(
    BlogStore store,
    BlogClock clock,
    TimeProvider time,
    IWebHostEnvironment environment,
    RequestLogStore requestLogStore,
    JumbotronMediaStore jumbotronMedia,
    ILogger<IndexModel> logger) : PageModel
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif",
    };

    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public bool IsAuthor { get; private set; }
    public string? PhotoUrl { get; private set; }
    public string? VideoUrl { get; private set; }
    public string? Lede { get; private set; }
    public BlogClock Clock => clock;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var media = jumbotronMedia.PickRandom();
        if (media is not null)
        {
            if (media.IsVideo)
                VideoUrl = media.Url;
            else
                PhotoUrl = media.Url;
        }
        else
        {
            // Fallback per le foto committate a mano in wwwroot/photos.
            PhotoUrl = PickPhoto();
        }

        try
        {
            Lede = (await requestLogStore.GetRandomLedeAsync(ct))?.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Impossibile leggere i lede da Sperone");
        }

        var now = time.GetUtcNow();
        IsAuthor = BlogAuth.IsAuthor(User);

        var all = await store.GetAllAsync(ct);
        Posts = all.Where(post => IsAuthor || post.IsVisibleAt(now)).ToList();
        Tags = Posts
            .SelectMany(post => post.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Una foto a caso per il jumbotron della home. Se la cartella non esiste o
    // è vuota si usa il gradiente di riserva (classe no-photo).
    private string? PickPhoto()
    {
        string webRoot = environment.WebRootPath
            ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        string directory = Path.Combine(webRoot, "photos");
        if (!Directory.Exists(directory))
            return null;

        string[] files = Directory.EnumerateFiles(directory)
            .Where(file => PhotoExtensions.Contains(Path.GetExtension(file)))
            .ToArray();

        if (files.Length == 0)
            return null;

        string name = Path.GetFileName(files[Random.Shared.Next(files.Length)]);
        return "/photos/" + Uri.EscapeDataString(name);
    }
}
