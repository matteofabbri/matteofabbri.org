using MainSite.Blog;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages;

public sealed class AboutModel(SitePageStore pages) : PageModel
{
    public SitePage PageContent { get; private set; } = default!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageContent = await pages.GetAsync(AboutPage.Slug, ct);
        ViewData["Description"] = PageContent.Description;
    }
}
