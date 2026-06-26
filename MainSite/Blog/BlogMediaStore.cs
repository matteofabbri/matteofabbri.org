using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace MainSite.Blog;

// Le immagini degli articoli. Stanno dentro la cartella del blog (quindi nel
// volume) e si servono solo da /blog/media/{nome}: niente path arbitrari dal
// browser, niente SVG (può contenere script), niente file sopra gli 8 MB.
public sealed class BlogMediaStore
{
    public const long MaxBytes = 8 * 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
    };

    private readonly string _directory;

    public BlogMediaStore(IOptions<BlogOptions> options, IHostEnvironment environment)
    {
        string configured = options.Value.ContentPath;
        string root = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));

        _directory = Path.Combine(root, "media");
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public IReadOnlyList<BlogMediaFile> List()
    {
        var files = new List<BlogMediaFile>();
        foreach (string path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (!AllowedTypes.ContainsKey(Path.GetExtension(name)))
                continue;

            var info = new FileInfo(path);
            files.Add(new BlogMediaFile(name, $"/blog/media/{name}", info.Length, new DateTimeOffset(info.LastWriteTimeUtc)));
        }

        return files.OrderByDescending(f => f.Modified).ToList();
    }

    // Il file viene prima scritto in una copia temporanea, poi controllato e
    // solo alla fine rinominato: non si serve mai un file a metà.
    public async Task<BlogMediaFile> SaveAsync(string originalFileName, Stream content, long length, CancellationToken ct = default)
    {
        if (length <= 0)
            throw new BlogValidationException("The file is empty.");
        if (length > MaxBytes)
            throw new BlogValidationException("The file exceeds 8 MB.");

        string extension = Path.GetExtension(originalFileName);
        if (!AllowedTypes.ContainsKey(extension))
            throw new BlogValidationException("Unsupported format. Use JPG, PNG, GIF, WebP or AVIF.");

        string baseName = BlogMarkdown.Slugify(Path.GetFileNameWithoutExtension(originalFileName));
        if (baseName.Length > 60)
            baseName = baseName[..60].TrimEnd('-');

        string id = RandomNumberGenerator.GetHexString(8).ToLowerInvariant();
        string name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{baseName}-{id}{extension.ToLowerInvariant()}";
        string target = Path.Combine(_directory, name);
        string temp = target + ".tmp";

        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                await content.CopyToAsync(file, ct);

            if (!await HasValidSignatureAsync(temp, extension.ToLowerInvariant(), ct))
                throw new BlogValidationException("The file content does not look like a valid image.");

            File.Move(temp, target);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        var info = new FileInfo(target);
        return new BlogMediaFile(name, $"/blog/media/{name}", info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    // Percorso sicuro di un file da servire, oppure null. Il nome deve essere
    // un semplice nome di file e la risoluzione deve restare dentro la cartella.
    public string? ResolvePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName != Path.GetFileName(fileName)
            || fileName.Contains('\\')
            || fileName.Contains('/'))
            return null;

        string full = Path.GetFullPath(Path.Combine(_directory, fileName));
        string prefix = _directory + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(full))
            return null;

        return full;
    }

    public string? ContentTypeFor(string fileName) =>
        AllowedTypes.TryGetValue(Path.GetExtension(fileName), out string? type) ? type : null;

    private static async Task<bool> HasValidSignatureAsync(string path, string extension, CancellationToken ct)
    {
        var header = new byte[16];
        await using var file = File.OpenRead(path);
        int read = await file.ReadAsync(header.AsMemory(), ct);

        return extension switch
        {
            ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                      && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A,
            ".gif" => read >= 6 && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8',
            ".webp" => read >= 12
                       && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                       && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P',
            ".avif" => read >= 12
                       && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p'
                       && header[8] == (byte)'a' && header[9] == (byte)'v' && header[10] == (byte)'i' && header[11] == (byte)'f',
            _ => false,
        };
    }
}
