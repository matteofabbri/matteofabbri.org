using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

public sealed class IndexModel(
    BlogStore store,
    SitePageStore pages,
    BlogClock clock,
    TimeProvider time) : PageModel
{
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public string AboutTitle { get; private set; } = AboutPage.DefaultTitle;
    public BlogClock Clock => clock;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Posts = await store.GetAllAsync(ct);
        AboutTitle = (await pages.GetAsync(AboutPage.Slug, ct)).Title;
    }

    public string Status(BlogPost post)
    {
        if (post.Draft)
            return "draft";
        return post.Published > time.GetUtcNow() ? "scheduled" : "published";
    }

    public async Task<IActionResult> OnPostDeleteAsync(string slug, CancellationToken ct)
    {
        if (await store.DeleteAsync(slug, ct))
            TempData["Messaggio"] = $"Post \"{slug}\" moved to the trash.";
        else
            TempData["Errore"] = "Post not found: it may have already been deleted.";

        return RedirectToPage();
    }
}
