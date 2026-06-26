using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MainSite.Blog;

// Gli articoli sono file Markdown con un front matter YAML, nella cartella
// configurata (in produzione un volume Docker). Niente database: il blog resta
// leggibile e modificabile anche con un editor di testo, e un deploy non porta
// via i contenuti perché stanno fuori dall'immagine.
public sealed class BlogStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly Regex SlugPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex HeadingPattern = new(@"^\s*#\s+(.+?)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly ILogger<BlogStore> _logger;
    private readonly BlogClock _clock;
    private readonly string _root;
    private readonly string _trash;
    private readonly ISerializer _yamlWriter;
    private readonly IDeserializer _yamlReader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public BlogStore(IOptions<BlogOptions> options, IHostEnvironment environment, BlogClock clock, ILogger<BlogStore> logger)
    {
        _logger = logger;
        _clock = clock;

        string configured = options.Value.ContentPath;
        _root = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        _trash = Path.Combine(_root, "_trash");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_trash);

        _yamlWriter = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        _yamlReader = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public string ContentRoot => _root;

    // Tutti gli articoli, bozze comprese. L'ordinamento è dal più recente: chi
    // chiama filtra la visibilità pubblica con IsVisibleAt.
    public async Task<IReadOnlyList<BlogPost>> GetAllAsync(CancellationToken ct = default)
    {
        var posts = new List<BlogPost>();
        foreach (string file in Directory.EnumerateFiles(_root, "*.md", SearchOption.TopDirectoryOnly))
        {
            string slug = Path.GetFileNameWithoutExtension(file);
            if (!SlugPattern.IsMatch(slug))
            {
                _logger.LogWarning("Articolo ignorato: il nome {File} non è uno slug valido.", file);
                continue;
            }

            var post = await ReadAsync(file, slug, ct);
            if (post is not null)
                posts.Add(post);
        }

        return posts
            .OrderByDescending(p => p.Published)
            .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<BlogPost?> GetAsync(string slug, CancellationToken ct = default)
    {
        if (!SlugPattern.IsMatch(slug))
            return null;

        string path = PostPath(slug);
        return File.Exists(path) ? await ReadAsync(path, slug, ct) : null;
    }

    public bool SlugExists(string slug) => SlugPattern.IsMatch(slug) && File.Exists(PostPath(slug));

    // Primo slug libero a partire da quello suggerito: "post", "post-2", ...
    // Serve quando il titolo genera un nome già usato.
    public string UniqueSlug(string baseSlug)
    {
        string slug = SlugPattern.IsMatch(baseSlug) ? baseSlug : "post";
        if (!SlugExists(slug))
            return slug;

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{slug}-{i}";
            if (!SlugExists(candidate))
                return candidate;
        }

        string random = $"{slug}-{Guid.NewGuid():N}";
        return random.Length > 80 ? random[..80] : random;
    }

    // Crea o aggiorna un articolo. Se lo slug cambia, il nuovo file viene
    // scritto PRIMA di spostare il vecchio nel cestino: un errore a metà non
    // deve lasciare l'articolo senza nessuna copia.
    public async Task<BlogPost> SaveAsync(BlogPostInput input, string? originalSlug = null, CancellationToken ct = default)
    {
        string title = (input.Title ?? "").Trim();
        string body = (input.Markdown ?? "").Trim();
        string summary = (input.Summary ?? "").Trim();
        string author = (input.Author ?? "").Trim();

        if (!SlugPattern.IsMatch(input.Slug))
            throw new BlogValidationException("The slug can contain only lowercase letters, numbers and hyphens.");
        if (title.Length == 0)
            throw new BlogValidationException("Title is required.");
        if (title.Length > 200)
            throw new BlogValidationException("Title cannot exceed 200 characters.");
        if (body.Length == 0)
            throw new BlogValidationException("The post body is required.");
        if (body.Length > 400_000)
            throw new BlogValidationException("The post is too long (limit 400,000 characters).");
        if (summary.Length > 300)
            throw new BlogValidationException("Summary cannot exceed 300 characters.");
        if (author.Length == 0)
            throw new BlogValidationException("Missing author: sign in again.");

        var tags = NormalizeTags(input.Tags);
        if (tags.Count > 12)
            throw new BlogValidationException("At most 12 tags per post.");
        if (tags.Any(t => t.Length > 40))
            throw new BlogValidationException("Each tag can be at most 40 characters.");

        await _writeLock.WaitAsync(ct);
        try
        {
            string path = PostPath(input.Slug);
            bool moving = originalSlug is not null
                && originalSlug != input.Slug
                && SlugPattern.IsMatch(originalSlug);

            if (File.Exists(path) && (originalSlug is null || originalSlug != input.Slug))
                throw new BlogValidationException($"A post with the slug \"{input.Slug}\" already exists.");

            var post = new BlogPost
            {
                Slug = input.Slug,
                Title = title,
                Summary = summary.Length > 0 ? summary : BlogMarkdown.Excerpt(body, 220),
                Markdown = body,
                Tags = tags,
                Published = input.Published,
                Updated = DateTimeOffset.UtcNow,
                Draft = input.Draft,
                Author = author,
            };

            await WriteAsync(path, post, ct);

            if (moving)
            {
                string oldPath = PostPath(originalSlug!);
                if (File.Exists(oldPath))
                    MoveToTrash(oldPath, originalSlug!);
            }

            return post;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Cancellazione "morbida": il file finisce in _trash, non sparisce. Se un
    // giorno serve recuperare un articolo cancellato per sbaglio, è lì.
    public async Task<bool> DeleteAsync(string slug, CancellationToken ct = default)
    {
        if (!SlugPattern.IsMatch(slug))
            return false;

        await _writeLock.WaitAsync(ct);
        try
        {
            string path = PostPath(slug);
            if (!File.Exists(path))
                return false;

            MoveToTrash(path, slug);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // --- Lettura e front matter ---------------------------------------------

    private async Task<BlogPost?> ReadAsync(string path, string slug, CancellationToken ct)
    {
        try
        {
            string text = await File.ReadAllTextAsync(path, ct);
            var parsed = ParseFrontMatter(text);

            if (parsed.Malformed)
            {
                _logger.LogWarning("Articolo {Slug} ignorato: front matter YAML non valido.", slug);
                return null;
            }

            var meta = parsed.Meta ?? new PostFrontMatter();
            string body = parsed.Body.Trim();
            string title = string.IsNullOrWhiteSpace(meta.Title) ? FirstHeading(body) ?? slug : meta.Title.Trim();
            var fallback = new DateTimeOffset(File.GetLastWriteTimeUtc(path));

            return new BlogPost
            {
                Slug = slug,
                Title = title,
                Summary = string.IsNullOrWhiteSpace(meta.Summary) ? BlogMarkdown.Excerpt(body, 220) : meta.Summary.Trim(),
                Markdown = body,
                Tags = NormalizeTags(meta.Tags),
                Published = ParseDate(meta.Published) ?? fallback,
                Updated = ParseDate(meta.Updated) ?? ParseDate(meta.Published) ?? fallback,
                Draft = meta.Draft ?? false,
                Author = meta.Author?.Trim() ?? "",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Articolo {Slug} ignorato: impossibile leggerlo.", slug);
            return null;
        }
    }

    private async Task WriteAsync(string path, BlogPost post, CancellationToken ct)
    {
        var meta = new PostFrontMatter
        {
            Title = post.Title,
            Summary = post.Summary,
            Published = FormatDate(post.Published),
            Updated = FormatDate(post.Updated),
            Tags = post.Tags.Count > 0 ? post.Tags.ToList() : null,
            Draft = post.Draft,
            Author = post.Author,
        };

        string yaml = _yamlWriter.Serialize(meta).TrimEnd('\r', '\n');
        string content = $"---\n{yaml}\n---\n\n{post.Markdown.TrimEnd()}\n";
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(temp, content, Utf8NoBom, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private void MoveToTrash(string path, string slug)
    {
        Directory.CreateDirectory(_trash);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string target = Path.Combine(_trash, $"{stamp}-{slug}.md");

        // Due cancellazioni nello stesso secondo non devono sovrascriversi.
        if (File.Exists(target))
            target = Path.Combine(_trash, $"{stamp}-{slug}-{Guid.NewGuid().ToString("N")[..6]}.md");

        File.Move(path, target, overwrite: true);
        _logger.LogInformation("Articolo {Slug} spostato nel cestino: {Target}", slug, target);
    }

    private string PostPath(string slug)
    {
        string path = Path.GetFullPath(Path.Combine(_root, slug + ".md"));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new BlogValidationException("Invalid post path.");
        return path;
    }

    private DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Front matter scritto a mano con la sola data: la si interpreta nel
        // fuso del blog, non in quello (UTC) della macchina. Va controllato
        // prima del parse generico, che accetterebbe "2026-10-23" dandogli
        // l'offset locale del server.
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return _clock.ToInstant(date);

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant))
        {
            // Se il front matter scritto a mano non porta l'offset, l'orario è
            // da intendere nel fuso del blog: il parser, da solo, userebbe
            // quello (UTC) della macchina.
            bool hasOffset = value.EndsWith('Z')
                || value.Contains('+')
                || value.LastIndexOf('-') > 10;
            return hasOffset ? instant : _clock.ToInstant(instant.DateTime);
        }

        return null;
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string? FirstHeading(string markdown)
    {
        var match = HeadingPattern.Match(markdown);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string tag = raw.Trim();
            if (tag.Length > 0 && seen.Add(tag))
                result.Add(tag);
        }

        return result;
    }

    // Front matter delimitato da due righe "---". Se il primo delimitatore non
    // c'è, il file è Markdown puro e i metadati si ricavano dal contenuto.
    private (PostFrontMatter? Meta, string Body, bool Malformed) ParseFrontMatter(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return (null, text, false);

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
            return (null, text, true);

        string yaml = string.Join('\n', lines[1..end]);
        string body = string.Join('\n', lines[(end + 1)..]);

        try
        {
            var meta = _yamlReader.Deserialize<PostFrontMatter>(yaml);
            return (meta ?? new PostFrontMatter(), body, false);
        }
        catch (YamlException)
        {
            return (null, body, true);
        }
    }

    // La forma esatta del front matter. Le date restano stringhe: le
    // interpretiamo noi nel fuso del blog, senza dipendere dal parser YAML.
    private sealed class PostFrontMatter
    {
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public string? Published { get; set; }
        public string? Updated { get; set; }
        public List<string>? Tags { get; set; }
        public bool? Draft { get; set; }
        public string? Author { get; set; }
    }
}
