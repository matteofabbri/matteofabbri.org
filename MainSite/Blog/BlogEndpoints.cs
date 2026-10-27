using System.Globalization;
using System.Xml.Linq;

namespace MainSite.Blog;

// Gli indirizzi "di servizio" del blog: il vecchio indice, il feed RSS e la
// sitemap. Sono minimal API perché non hanno bisogno di una view.
public static class BlogEndpoints
{
    public static void MapBlogEndpoints(this WebApplication app)
    {
        // La home è l'indice del blog (Pages/Blog/Index.cshtml, route "/").
        // Il vecchio indirizzo resta solo come redirect per segnalibri e feed.
        app.MapGet("/blog", () => Results.Redirect("/", permanent: true));

        app.MapGet("/blog/feed.xml", async (
            BlogStore store, TimeProvider clock, HttpRequest request, CancellationToken ct) =>
        {
            var posts = PublicPosts(await store.GetAllAsync(ct), clock).Take(20).ToList();
            string baseUrl = BaseUrl(request);
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + BuildFeed(baseUrl, posts).ToString();
            return Results.Text(xml, "application/rss+xml; charset=utf-8");
        });

        app.MapGet("/sitemap.xml", async (
            BlogStore store, TimeProvider clock, HttpRequest request, CancellationToken ct) =>
        {
            var posts = PublicPosts(await store.GetAllAsync(ct), clock).ToList();
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + BuildSitemap(BaseUrl(request), posts).ToString();
            return Results.Text(xml, "application/xml; charset=utf-8");
        });
    }

    private static IEnumerable<BlogPost> PublicPosts(IReadOnlyList<BlogPost> posts, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return posts.Where(p => p.IsVisibleAt(now));
    }

    private static string BaseUrl(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}";

    private static XDocument BuildFeed(string baseUrl, IReadOnlyList<BlogPost> posts)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        string feedUrl = $"{baseUrl}/blog/feed.xml";
        var now = DateTimeOffset.UtcNow;

        return new XDocument(
            new XElement("rss",
                new XAttribute("version", "2.0"),
                new XAttribute(XNamespace.Xmlns + "atom", atom),
                new XElement("channel",
                    new XElement("title", "Matteo Fabbri — Blog"),
                    new XElement("link", $"{baseUrl}/"),
                    new XElement("description", "Notes, projects and things worth telling."),
                    new XElement("language", "en"),
                    new XElement("lastBuildDate", now.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
                    new XElement(atom + "link",
                        new XAttribute("href", feedUrl),
                        new XAttribute("rel", "self"),
                        new XAttribute("type", "application/rss+xml")),
                    posts.Select(post => new XElement("item",
                        new XElement("title", post.Title),
                        new XElement("link", $"{baseUrl}/blog/{post.Slug}"),
                        new XElement("guid", new XAttribute("isPermaLink", "true"), $"{baseUrl}/blog/{post.Slug}"),
                        new XElement("pubDate", post.Published.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
                        new XElement("description", post.Summary),
                        post.Tags.Select(tag => new XElement("category", tag)))))));
    }

    private static XDocument BuildSitemap(string baseUrl, IReadOnlyList<BlogPost> posts)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        return new XDocument(
            new XElement(ns + "urlset",
                new XElement(ns + "url",
                    new XElement(ns + "loc", $"{baseUrl}/")),
                new XElement(ns + "url",
                    new XElement(ns + "loc", $"{baseUrl}/requests")),
                new XElement(ns + "url",
                    new XElement(ns + "loc", $"{baseUrl}/info-bot")),
                posts.Select(post => new XElement(ns + "url",
                    new XElement(ns + "loc", $"{baseUrl}/blog/{post.Slug}"),
                    new XElement(ns + "lastmod", post.Updated.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))),
                posts.SelectMany(post => post.Tags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .Select(tag => new XElement(ns + "url",
                        new XElement(ns + "loc", $"{baseUrl}/blog/tag/{BlogMarkdown.Slugify(tag)}")))));
    }
}
