using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

public sealed class SitePageModel(SitePageStore store, ILogger<SitePageModel> logger) : PageModel
{
    [BindProperty]
    public SitePageInput Input { get; set; } = new();

    public string Slug { get; private set; } = AboutPage.Slug;

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        if (!SitePageStore.IsSupported(slug))
            return NotFound();

        Slug = slug;
        var page = await store.GetAsync(slug, ct);
        Input = new SitePageInput
        {
            Title = page.Title,
            Description = page.Description,
            Markdown = page.Markdown,
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        if (!SitePageStore.IsSupported(slug))
            return NotFound();

        Slug = slug;
        Input.Title = (Input.Title ?? "").Trim();
        Input.Description = (Input.Description ?? "").Trim();
        Input.Markdown ??= "";

        if (Input.Title.Length == 0)
            ModelState.AddModelError("Input.Title", "Title is required.");
        else if (Input.Title.Length > 200)
            ModelState.AddModelError("Input.Title", "Title cannot exceed 200 characters.");

        if (Input.Description.Length > 300)
            ModelState.AddModelError("Input.Description", "Lede cannot exceed 300 characters.");

        if (string.IsNullOrWhiteSpace(Input.Markdown))
            ModelState.AddModelError("Input.Markdown", "The page body is required.");

        if (!ModelState.IsValid)
            return Page();

        try
        {
            await store.SaveAsync(slug, Input.Title, Input.Description, Input.Markdown, ct);
        }
        catch (BlogValidationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Page {Slug} could not be saved.", slug);
            ModelState.AddModelError("", "The page could not be saved. Check the site log for details.");
            return Page();
        }

        TempData["Messaggio"] = "Page saved. It is live now: no deploy needed.";
        return RedirectToPage("SitePage", new { slug });
    }
}

public sealed class SitePageInput
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Markdown { get; set; }
}
