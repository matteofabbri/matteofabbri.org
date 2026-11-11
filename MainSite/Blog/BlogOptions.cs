namespace MainSite.Blog;

// Configuration for the "Blog" section of appsettings.json. Relative paths
// resolve against the content root (the project folder in development, /app in
// production); on the server the volume is mounted at /data/blog and the value
// comes from Blog__ContentPath.
public sealed class BlogOptions
{
    public const string Section = "Blog";

    public string ContentPath { get; set; } = "App_Data/blog";

    // Time zone used to show and interpret dates in the writing area. The server
    // runs in UTC: without this, "23 October 09:00" would be ambiguous.
    public string TimeZone { get; set; } = "Europe/Rome";

    // Name shown to readers. The author email stays in the post metadata but is
    // not what we print under the article.
    public string AuthorName { get; set; } = "Matteo Fabbri";
}
