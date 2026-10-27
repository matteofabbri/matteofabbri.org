using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MainSite.Blog;

// Foto e video della pagina 404: <Blog:ContentPath>/404, serviti da
// /404-media/{nome}.
public sealed class NotFoundMediaStore
{
    private readonly MediaLibrary _library;

    public NotFoundMediaStore(IOptions<BlogOptions> options, IHostEnvironment environment)
    {
        _library = new MediaLibrary(
            MediaLibrary.BlogDirectory(options, environment, "404"),
            "/404-media");
    }

    public IReadOnlyList<MediaFile> List() => _library.List();
    public MediaFile? PickRandom() => _library.PickRandom();
    public Task<MediaFile> SaveAsync(string originalFileName, Stream content, long length, CancellationToken ct = default) =>
        _library.SaveAsync(originalFileName, content, length, ct);
    public bool Delete(string fileName) => _library.Delete(fileName);
    public string? ResolvePath(string fileName) => _library.ResolvePath(fileName);
    public string? ContentTypeFor(string fileName) => _library.ContentTypeFor(fileName);
}
