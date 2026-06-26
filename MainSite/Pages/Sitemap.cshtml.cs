using MainSite.Blog;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages;

// Sitemap leggibile: pagine fisse, articoli pubblicati e tag.
public sealed class SitemapModel(BlogStore store, BlogClock clock, TimeProvider time) : PageModel
{
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public BlogClock Clock => clock;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var all = await store.GetAllAsync(ct);
        Posts = all.Where(post => post.IsVisibleAt(now)).ToList();
        Tags = Posts
            .SelectMany(post => post.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
