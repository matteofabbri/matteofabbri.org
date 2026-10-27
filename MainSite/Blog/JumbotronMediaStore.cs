using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MainSite.Blog;

// Foto e video del jumbotron: <Blog:ContentPath>/jumbotron, serviti da
// /jumbotron/media/{nome}.
public sealed class JumbotronMediaStore
{
    private readonly MediaLibrary _library;

    public JumbotronMediaStore(IOptions<BlogOptions> options, IHostEnvironment environment)
    {
        _library = new MediaLibrary(
            MediaLibrary.BlogDirectory(options, environment, "jumbotron"),
            "/jumbotron/media");
    }

    public IReadOnlyList<MediaFile> List() => _library.List();
    public MediaFile? PickRandom() => _library.PickRandom();
    public Task<MediaFile> SaveAsync(string originalFileName, Stream content, long length, CancellationToken ct = default) =>
        _library.SaveAsync(originalFileName, content, length, ct);
    public bool Delete(string fileName) => _library.Delete(fileName);
    public string? ResolvePath(string fileName) => _library.ResolvePath(fileName);
    public string? ContentTypeFor(string fileName) => _library.ContentTypeFor(fileName);
}
