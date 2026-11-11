using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Blog;

public sealed class TagModel(BlogStore store, BlogClock clock, TimeProvider time) : PageModel
{
    public string TagName { get; private set; } = "";
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public bool IsAuthor { get; private set; }
    public BlogClock Clock => clock;

    public async Task<IActionResult> OnGetAsync(string tag, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        IsAuthor = BlogAuth.IsAuthor(User);

        var all = await store.GetAllAsync(ct);
        var visible = all.Where(post => IsAuthor || post.IsVisibleAt(now)).ToList();

        Posts = visible
            .Where(post => post.Tags.Any(t => string.Equals(
                BlogMarkdown.Slugify(t), tag, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        string? name = visible
            .SelectMany(post => post.Tags)
            .FirstOrDefault(t => string.Equals(BlogMarkdown.Slugify(t), tag, StringComparison.OrdinalIgnoreCase));

        if (name is null)
            return NotFound();

        TagName = name;
        return Page();
    }
}
