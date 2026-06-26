using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MainSite.Blog;

// Un file gestito dall'admin (jumbotron o 404): foto o video.
public sealed record MediaFile(
    string FileName,
    string Url,
    bool IsVideo,
    long Size,
    DateTimeOffset Modified)
{
    public string SizeLabel => FormatSize(Size);

    public static string FormatSize(long size) => size switch
    {
        >= 1_048_576 => (size / 1_048_576.0).ToString("0.0", CultureInfo.GetCultureInfo("en-GB")) + " MB",
        >= 1_024 => (size / 1_024.0).ToString("0", CultureInfo.GetCultureInfo("en-GB")) + " KB",
        _ => size + " B",
    };
}

// Cartella di foto/video caricati dall'admin, con validazione di formato e
// firma. Il percorso vive nel volume del blog; ogni consumer decide cartella e
// prefisso URL (jumbotron, 404, ...).
public sealed class MediaLibrary
{
    public const long MaxImageBytes = 15 * 1024 * 1024;
    public const long MaxVideoBytes = 25 * 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        [".m4v"] = "video/x-m4v",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".m4v",
    };

    private readonly string _directory;
    private readonly string _urlPrefix;

    public MediaLibrary(string directory, string urlPrefix)
    {
        _directory = directory;
        _urlPrefix = urlPrefix.TrimEnd('/');
        Directory.CreateDirectory(_directory);
    }

    public static string BlogDirectory(
        IOptions<BlogOptions> options,
        IHostEnvironment environment,
        string folder)
    {
        string configured = options.Value.ContentPath;
        string root = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));

        return Path.Combine(root, folder);
    }

    public IReadOnlyList<MediaFile> List()
    {
        var files = new List<MediaFile>();
        foreach (string path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (!AllowedTypes.TryGetValue(Path.GetExtension(name), out _))
                continue;

            var info = new FileInfo(path);
            files.Add(ToFile(name, info.Length, info.LastWriteTimeUtc));
        }

        return files.OrderByDescending(file => file.Modified).ToList();
    }

    public MediaFile? PickRandom()
    {
        var files = List();
        return files.Count == 0 ? null : files[Random.Shared.Next(files.Count)];
    }

    public async Task<MediaFile> SaveAsync(
        string originalFileName,
        Stream content,
        long length,
        CancellationToken ct = default)
    {
        string extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!AllowedTypes.TryGetValue(extension, out _))
            throw new BlogValidationException("Unsupported format. Use JPG, PNG, GIF, WebP, AVIF, MP4, WebM, MOV or M4V.");

        bool isVideo = VideoExtensions.Contains(extension);
        long maxBytes = isVideo ? MaxVideoBytes : MaxImageBytes;

        if (length <= 0)
            throw new BlogValidationException("The file is empty.");
        if (length > maxBytes)
            throw new BlogValidationException(isVideo
                ? "The video exceeds 25 MB."
                : "The image exceeds 15 MB.");

        string baseName = BlogMarkdown.Slugify(Path.GetFileNameWithoutExtension(originalFileName));
        if (baseName.Length > 50)
            baseName = baseName[..50].TrimEnd('-');

        string id = RandomNumberGenerator.GetHexString(8).ToLowerInvariant();
        string name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{baseName}-{id}{extension}";
        string target = Path.Combine(_directory, name);
        string temp = target + ".tmp";

        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                await content.CopyToAsync(file, ct);

            if (!await HasValidSignatureAsync(temp, extension, ct))
                throw new BlogValidationException("The file content does not look like a valid image or video.");

            File.Move(temp, target);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        var info = new FileInfo(target);
        return ToFile(name, info.Length, info.LastWriteTimeUtc);
    }

    // Copia un file già presente su disco (per esempio dalla cartella content)
    // mantenendo il nome: se esiste già non lo tocca. Il formato e la firma
    // sono controllati come per un upload, così nella cartella 404 finisce solo
    // roba che il browser sa mostrare.
    public async Task<MediaFile> ImportAsync(string sourcePath, CancellationToken ct = default)
    {
        string name = Path.GetFileName(sourcePath);
        string extension = Path.GetExtension(name).ToLowerInvariant();
        if (!AllowedTypes.ContainsKey(extension))
            throw new BlogValidationException("Unsupported format. Use JPG, PNG, GIF, WebP, AVIF, MP4, WebM, MOV or M4V.");

        string target = Path.Combine(_directory, name);
        string temp = target + ".tmp";

        try
        {
            await using (var input = File.OpenRead(sourcePath))
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                await input.CopyToAsync(file, ct);

            if (!await HasValidSignatureAsync(temp, extension, ct))
                throw new BlogValidationException("The file content does not look like a valid image or video.");

            File.Move(temp, target);
        }
        catch (IOException) when (File.Exists(target))
        {
            throw new BlogValidationException("A media file with this name is already here.");
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        var info = new FileInfo(target);
        return ToFile(name, info.Length, info.LastWriteTimeUtc);
    }

    public bool Exists(string fileName) => ResolvePath(fileName) is not null;

    public bool Delete(string fileName)
    {
        string? path = ResolvePath(fileName);
        if (path is null)
            return false;

        File.Delete(path);
        return true;
    }

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

    private MediaFile ToFile(string name, long size, DateTime modified) =>
        new(
            name,
            _urlPrefix + "/" + Uri.EscapeDataString(name),
            VideoExtensions.Contains(Path.GetExtension(name)),
            size,
            new DateTimeOffset(modified, TimeSpan.Zero));

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
            ".mp4" or ".mov" or ".m4v" => read >= 8
                       && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p',
            ".webm" => read >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3,
            _ => false,
        };
    }
}
