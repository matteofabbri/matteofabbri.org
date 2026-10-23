using MainSite.Blog;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Blog;

public sealed class IndexModel(BlogStore store, BlogClock clock, TimeProvider time, BlogAuthState auth) : PageModel
{
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public bool IsAuthor { get; private set; }
    public BlogClock Clock => clock;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        IsAuthor = auth.IsAuthor(User);

        var all = await store.GetAllAsync(ct);
        Posts = all.Where(post => IsAuthor || post.IsVisibleAt(now)).ToList();
        Tags = Posts
            .SelectMany(post => post.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
