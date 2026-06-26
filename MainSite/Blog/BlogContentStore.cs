using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MainSite.Blog;

// Un file scaricato nella cartella "content" (nessun URL pubblico: quella
// cartella è solo un deposito da cui copiare verso 404).
public sealed record ContentFile(string FileName, long Size, DateTimeOffset Modified)
{
    public string SizeLabel => MediaFile.FormatSize(Size);
}

public sealed record ContentCopyResult(int Copied, IReadOnlyList<string> Skipped);

// <Blog:ContentPath>/content: dove finiscono i video presi da yt-dlp. Sta
// accanto a media/ e 404/ nel volume del blog, quindi sopravvive ai deploy.
public sealed class BlogContentStore
{
    private readonly string _directory;

    public BlogContentStore(IOptions<BlogOptions> options, IHostEnvironment environment)
    {
        _directory = MediaLibrary.BlogDirectory(options, environment, "content");
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public IReadOnlyList<ContentFile> List()
    {
        var files = new List<ContentFile>();
        foreach (string path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (IsPartial(name))
                continue;

            var info = new FileInfo(path);
            files.Add(new ContentFile(name, info.Length, new DateTimeOffset(info.LastWriteTimeUtc)));
        }

        return files.OrderByDescending(file => file.Modified).ToList();
    }

    // Copia tutto quello che c'è in content dentro la libreria 404. I file già
    // presenti (stesso nome) e i formati che la pagina 404 non sa mostrare
    // vengono saltati e riportati, non fanno fallire il resto.
    public async Task<ContentCopyResult> CopyToAsync(NotFoundMediaStore target, CancellationToken ct = default)
    {
        int copied = 0;
        var skipped = new List<string>();

        foreach (var file in List())
        {
            try
            {
                if (target.Exists(file.FileName))
                {
                    skipped.Add($"{file.FileName} (already in 404)");
                    continue;
                }

                await target.ImportAsync(Path.Combine(_directory, file.FileName), ct);
                copied++;
            }
            catch (BlogValidationException ex)
            {
                skipped.Add($"{file.FileName} ({ex.Message})");
            }
        }

        return new ContentCopyResult(copied, skipped);
    }

    // yt-dlp scrive prima un .part e lo rinomina alla fine: i mezzi file non
    // devono comparire nell'elenco.
    private static bool IsPartial(string name) =>
        name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
}
