using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Options;

namespace MainSite.Blog;

// I link "media" del corpo di un articolo: quelli inseriti dall'editor con i
// pulsanti immagine/link. Restano fuori i file già nostri (il sito stesso) e le
// immagini: yt-dlp serve per i video e l'audio, non per scaricare foto.
public static class PostMedia
{
    // Il rendering degli articoli disattiva l'HTML grezzo, ma qui l'HTML non
    // serve: bastano link e autolink del Markdown.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".svg", ".bmp", ".ico", ".pdf",
    };

    // URL esterni http(s) del documento, senza duplicati e nell'ordine in cui
    // compaiono. ownHost esclude i link che puntano al sito stesso.
    public static IReadOnlyList<string> ExtractUrls(string? markdown, string? ownHost = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var document = Markdown.Parse(markdown, Pipeline);
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in document.Descendants<LinkInline>())
            Add(link.Url, ownHost, urls, seen);

        foreach (var link in document.Descendants<AutolinkInline>())
            Add(link.Url, ownHost, urls, seen);

        return urls;
    }

    private static void Add(string? value, string? ownHost, List<string> urls, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return;

        if (!string.IsNullOrWhiteSpace(ownHost) && IsSameHost(uri.Host, ownHost))
            return;

        if (SkipExtensions.Contains(Path.GetExtension(uri.AbsolutePath)))
            return;

        if (seen.Add(uri.AbsoluteUri))
            urls.Add(uri.AbsoluteUri);
    }

    private static bool IsSameHost(string host, string ownHost) =>
        host.Equals(ownHost, StringComparison.OrdinalIgnoreCase)
        || host.Equals("www." + ownHost, StringComparison.OrdinalIgnoreCase)
        || ownHost.Equals("www." + host, StringComparison.OrdinalIgnoreCase);
}

// Un link elaborato: riuscito (con i file creati) oppure fallito con il motivo.
public sealed record PostMediaItem(
    string Url,
    bool Success,
    IReadOnlyList<string> Files,
    string? Error);

public sealed record PostMediaDownloadResult(
    IReadOnlyList<PostMediaItem> Items,
    int IgnoredUrls)
{
    public int DownloadedFiles => Items.Sum(item => item.Files.Count);
    public IReadOnlyList<PostMediaItem> Failures =>
        Items.Where(item => !item.Success).ToList();
    public IReadOnlyList<PostMediaItem> AlreadyPresent =>
        Items.Where(item => item.Success && item.Files.Count == 0).ToList();
}

// Prende il Markdown di un articolo e passa i link trovati al wrapper di
// yt-dlp, uno alla volta, salvando tutto nella cartella "content".
public sealed class PostMediaDownloader(
    YtDlp ytDlp,
    BlogContentStore content,
    IOptions<YtDlpOptions> options,
    ILogger<PostMediaDownloader> logger)
{
    public async Task<PostMediaDownloadResult> DownloadAsync(
        string? markdown,
        string? ownHost,
        CancellationToken ct = default)
    {
        var urls = PostMedia.ExtractUrls(markdown, ownHost);
        int limit = Math.Max(1, options.Value.MaxLinksPerRun);
        var selected = urls.Take(limit).ToList();
        var items = new List<PostMediaItem>(selected.Count);

        foreach (string url in selected)
        {
            try
            {
                var files = await ytDlp.DownloadAsync(url, content.DirectoryPath, ct);
                items.Add(new PostMediaItem(url, true, files.Select(file => file.FileName).ToList(), null));
            }
            catch (YtDlpException ex)
            {
                logger.LogWarning(ex, "yt-dlp non è riuscito a scaricare {Url}", url);
                items.Add(new PostMediaItem(url, false, [], ex.Message));
            }
        }

        return new PostMediaDownloadResult(items, urls.Count - selected.Count);
    }
}
