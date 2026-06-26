using System.Globalization;
using System.Security.Claims;
using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MainSite.Pages.Admin;

public sealed class EditModel(
    BlogStore store,
    BlogClock clock,
    IOptions<BlogOptions> options,
    IOptions<YtDlpOptions> ytDlpOptions,
    PostMediaDownloader media) : PageModel
{
    [BindProperty]
    public EditInput Input { get; set; } = new();

    public string? OriginalSlug { get; private set; }
    public bool IsNew => OriginalSlug is null;
    public BlogClock Clock => clock;
    public string AuthorName => options.Value.AuthorName;

    public async Task<IActionResult> OnGetAsync(string? slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            // Un articolo nuovo parte come bozza: pubblicarlo deve essere una
            // scelta esplicita, non l'effetto di un clic di troppo.
            Input = new EditInput
            {
                Published = clock.ToLocal(DateTimeOffset.UtcNow).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                Draft = true,
            };
            return Page();
        }

        var post = await store.GetAsync(slug, ct);
        if (post is null)
            return NotFound();

        OriginalSlug = post.Slug;
        Input = new EditInput
        {
            Title = post.Title,
            Slug = post.Slug,
            Summary = post.Summary,
            Tags = string.Join(", ", post.Tags),
            Published = clock.ToLocal(post.Published).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            Draft = post.Draft,
            Markdown = post.Markdown,
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? slug, CancellationToken ct)
    {
        OriginalSlug = slug;

        Input.Title = (Input.Title ?? "").Trim();
        Input.Slug = (Input.Slug ?? "").Trim();
        Input.Summary = (Input.Summary ?? "").Trim();
        Input.Tags = (Input.Tags ?? "").Trim();
        Input.Markdown ??= "";

        if (Input.Title.Length == 0)
            ModelState.AddModelError("Input.Title", "Title is required.");
        else if (Input.Title.Length > 200)
            ModelState.AddModelError("Input.Title", "Title cannot exceed 200 characters.");

        if (string.IsNullOrWhiteSpace(Input.Markdown))
            ModelState.AddModelError("Input.Markdown", "The post body is required.");

        if (Input.Summary.Length > 300)
            ModelState.AddModelError("Input.Summary", "Summary cannot exceed 300 characters.");

        string finalSlug = Input.Slug.Length > 0
            ? BlogMarkdown.Slugify(Input.Slug)
            : slug ?? store.UniqueSlug(BlogMarkdown.Slugify(Input.Title));

        if (!BlogMarkdown.IsValidSlug(finalSlug))
            ModelState.AddModelError("Input.Slug", "Slug is not valid.");

        if (finalSlug != slug && store.SlugExists(finalSlug))
            ModelState.AddModelError("Input.Slug", "A post with this slug already exists: choose another one.");

        DateTimeOffset published = default;
        if (!DateTime.TryParseExact(
                Input.Published,
                new[] { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            ModelState.AddModelError("Input.Published", "Invalid publication date.");
        }
        else
        {
            published = clock.ToInstant(local);
        }

        string author = User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.Identity?.Name
            ?? "";

        if (author.Length == 0)
            ModelState.AddModelError("", "The session has no email address: sign in again.");

        if (!ModelState.IsValid)
            return Page();

        var tags = Input.Tags
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            var post = await store.SaveAsync(new BlogPostInput
            {
                Slug = finalSlug,
                Title = Input.Title,
                Summary = Input.Summary,
                Markdown = Input.Markdown,
                Tags = tags,
                Published = published,
                Draft = Input.Draft,
                Author = author,
            }, slug, ct);

            TempData["Messaggio"] = Input.Draft ? "Draft saved." : "Post saved.";
            return RedirectToPage("Edit", new { slug = post.Slug });
        }
        catch (BlogValidationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDownloadMediaAsync(string? slug, CancellationToken ct)
    {
        OriginalSlug = slug;
        Input.Markdown ??= "";

        var result = await media.DownloadAsync(Input.Markdown, Request.Host.Host, ct);

        if (result.Items.Count == 0)
        {
            TempData["Errore"] =
                "No external media links found in the body: insert a video link in the editor and try again. "
                + "Links to this site and direct image files are skipped.";
            return Page();
        }

        var successes = new List<string>();
        var files = result.Items.SelectMany(item => item.Files).ToList();
        if (files.Count > 0)
        {
            string shown = string.Join(", ", files.Take(5));
            successes.Add(files.Count > 5
                ? $"{files.Count} files downloaded into content/: {shown}, …"
                : $"{files.Count} file(s) downloaded into content/: {shown}");
        }
        if (result.AlreadyPresent.Count > 0)
            successes.Add($"{result.AlreadyPresent.Count} link(s) already in content");
        if (result.IgnoredUrls > 0)
            successes.Add($"{result.IgnoredUrls} link(s) ignored (at most {ytDlpOptions.Value.MaxLinksPerRun} per run)");

        TempData["Messaggio"] = string.Join(". ", successes) + ".";

        if (result.Failures.Count > 0)
        {
            var failures = result.Failures
                .Take(3)
                .Select(item => $"{item.Url}: {item.Error}")
                .ToList();
            if (result.Failures.Count > failures.Count)
                failures.Add($"and {result.Failures.Count - failures.Count} more");

            TempData["Errore"] = $"yt-dlp could not download {result.Failures.Count} link(s). " + string.Join(" ", failures);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string? slug, CancellationToken ct)
    {
        if (slug is not null && await store.DeleteAsync(slug, ct))
            TempData["Messaggio"] = $"Post \"{slug}\" moved to the trash.";
        else
            TempData["Errore"] = "Post not found: it may have already been deleted.";

        return RedirectToPage("Index");
    }
}

public sealed class EditInput
{
    public string? Title { get; set; }
    public string? Slug { get; set; }
    public string? Summary { get; set; }
    public string? Tags { get; set; }
    public string? Published { get; set; }
    public bool Draft { get; set; }
    public string? Markdown { get; set; }
}
