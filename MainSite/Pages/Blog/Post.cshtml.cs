using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MainSite.Pages.Blog;

public sealed class PostModel(BlogStore store, BlogClock clock, TimeProvider time, IOptions<BlogOptions> options) : PageModel
{
    public BlogPost Post { get; private set; } = default!;
    public string Html { get; private set; } = "";
    public bool IsPreview { get; private set; }
    public bool IsAuthor { get; private set; }
    public BlogClock Clock => clock;
    public string AuthorName => options.Value.AuthorName;

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        var post = await store.GetAsync(slug, ct);
        if (post is null)
            return NotFound();

        var now = time.GetUtcNow();
        IsAuthor = BlogAuth.IsAuthor(User);
        if (!post.IsVisibleAt(now) && !IsAuthor)
            return NotFound();

        Post = post;
        IsPreview = !post.IsVisibleAt(now);
        Html = BlogMarkdown.ToHtml(post.Markdown);
        return Page();
    }
}
