using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

public sealed class IndexModel(BlogStore store, BlogClock clock, TimeProvider time) : PageModel
{
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public BlogClock Clock => clock;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Posts = await store.GetAllAsync(ct);
    }

    public string Status(BlogPost post)
    {
        if (post.Draft)
            return "bozza";
        return post.Published > time.GetUtcNow() ? "programmato" : "pubblicato";
    }

    public async Task<IActionResult> OnPostDeleteAsync(string slug, CancellationToken ct)
    {
        if (await store.DeleteAsync(slug, ct))
            TempData["Messaggio"] = $"Articolo \"{slug}\" spostato nel cestino.";
        else
            TempData["Errore"] = "Articolo non trovato: forse è già stato cancellato.";

        return RedirectToPage();
    }
}
