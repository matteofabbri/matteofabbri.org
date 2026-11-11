using System.Text;
using Microsoft.Extensions.Options;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MainSite.Blog;

// A Markdown page that is not a blog post: it has a title, a lede and a body,
// and it is stored on the same volume as the posts so /admin can edit it without
// a deploy. The file is the source of truth; the built-in copy only seeds an
// empty volume.
public sealed record SitePage
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public string Markdown { get; init; } = "";
    public DateTimeOffset Updated { get; init; }
}

public sealed class SitePageStore
{
    private const string PagesDirectory = "pages";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _directory;
    private readonly ILogger<SitePageStore> _logger;
    private readonly ISerializer _yamlWriter;
    private readonly IDeserializer _yamlReader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SitePageStore(IOptions<BlogOptions> options, IHostEnvironment environment, ILogger<SitePageStore> logger)
    {
        _logger = logger;

        string configured = options.Value.ContentPath;
        string root = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        _directory = Path.Combine(root, PagesDirectory);

        _yamlWriter = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        _yamlReader = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public string DirectoryPath => _directory;

    public static bool IsSupported(string slug) => slug == AboutPage.Slug;

    public async Task<SitePage> GetAsync(string slug, CancellationToken ct = default)
    {
        EnsureSupported(slug);

        string path = PathFor(slug);
        try
        {
            if (!File.Exists(path))
                return Default(slug);

            string text = await File.ReadAllTextAsync(path, ct);
            var parsed = ParseFrontMatter(text);
            if (parsed.Malformed)
            {
                _logger.LogWarning("Page {Slug} ignored: the YAML front matter is not valid.", slug);
                return Default(slug);
            }

            var fallback = Default(slug);
            var meta = parsed.Meta ?? new SitePageFrontMatter();
            string body = parsed.Body.Trim();

            return new SitePage
            {
                Slug = slug,
                Title = string.IsNullOrWhiteSpace(meta.Title) ? fallback.Title : meta.Title.Trim(),
                Description = string.IsNullOrWhiteSpace(meta.Description) ? fallback.Description : meta.Description.Trim(),
                Markdown = body.Length > 0 ? body : fallback.Markdown,
                Updated = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Page {Slug} could not be read; using the built-in copy.", slug);
            return Default(slug);
        }
    }

    public async Task<SitePage> SaveAsync(
        string slug, string title, string description, string markdown, CancellationToken ct = default)
    {
        EnsureSupported(slug);

        title = (title ?? "").Trim();
        description = (description ?? "").Trim();
        markdown = (markdown ?? "").Trim();

        if (title.Length == 0)
            throw new BlogValidationException("Title is required.");
        if (title.Length > 200)
            throw new BlogValidationException("Title cannot exceed 200 characters.");
        if (description.Length > 300)
            throw new BlogValidationException("Lede cannot exceed 300 characters.");
        if (markdown.Length == 0)
            throw new BlogValidationException("The page body is required.");

        await _writeLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_directory);

            string path = PathFor(slug);
            string temp = path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, BuildFile(title, description, markdown), Utf8NoBom, ct);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }

            return new SitePage
            {
                Slug = slug,
                Title = title,
                Description = description,
                Markdown = markdown,
                Updated = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // On a brand-new volume the page file does not exist yet. Write the built-in
    // copy once; after that the file belongs to the editor and is never touched.
    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        string path = PathFor(AboutPage.Slug);
        if (File.Exists(path))
            return;

        try
        {
            await SaveAsync(AboutPage.Slug, AboutPage.DefaultTitle, AboutPage.DefaultDescription, AboutPage.DefaultMarkdown, ct);
            _logger.LogInformation("Seeded the editable page {Slug}.", AboutPage.Slug);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Page {Slug} could not be seeded; the site will use the built-in copy.", AboutPage.Slug);
        }
    }

    private static SitePage Default(string slug) => new()
    {
        Slug = slug,
        Title = AboutPage.DefaultTitle,
        Description = AboutPage.DefaultDescription,
        Markdown = AboutPage.DefaultMarkdown,
        Updated = DateTimeOffset.MinValue,
    };

    private static void EnsureSupported(string slug)
    {
        if (!IsSupported(slug))
            throw new BlogValidationException($"Unknown page \"{slug}\".");
    }

    private string PathFor(string slug)
    {
        if (!BlogMarkdown.IsValidSlug(slug))
            throw new BlogValidationException("Slug is not valid.");

        return Path.Combine(_directory, slug + ".md");
    }

    private string BuildFile(string title, string description, string markdown)
    {
        string yaml = _yamlWriter.Serialize(new SitePageFrontMatter
        {
            Title = title,
            Description = description,
        }).TrimEnd();

        return $"---\n{yaml}\n---\n\n{markdown}\n";
    }

    // Front matter delimited by two "---" lines; without it the whole file is
    // the body and the title falls back to the built-in copy.
    private (SitePageFrontMatter? Meta, string Body, bool Malformed) ParseFrontMatter(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return (null, text.Trim(), false);

        int end = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                end = i;
                break;
            }
        }

        if (end < 0)
            return (null, text.Trim(), true);

        string yaml = string.Join('\n', lines[1..end]);
        string body = string.Join('\n', lines[(end + 1)..]);

        try
        {
            var meta = _yamlReader.Deserialize<SitePageFrontMatter>(yaml);
            return (meta ?? new SitePageFrontMatter(), body, false);
        }
        catch (YamlException)
        {
            return (null, body, true);
        }
    }

    private sealed class SitePageFrontMatter
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
    }
}
