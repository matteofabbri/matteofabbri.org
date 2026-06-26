using MainSite.Blog;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class NotFoundMediaStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "notfound-media-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Salva_nella_cartella_404_e_usa_l_url_dedicata()
    {
        var store = new NotFoundMediaStore(
            Options.Create(new BlogOptions { ContentPath = _path }),
            new TestHostEnvironment(_path));

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0];
        using var stream = new MemoryStream(png);
        var file = await store.SaveAsync("404.png", stream, png.Length);

        Assert.StartsWith("/404-media/", file.Url);
        Assert.Contains(Path.Combine("404"), store.ResolvePath(file.FileName)!);
        Assert.Equal("image/png", store.ContentTypeFor(file.FileName));
        Assert.Single(store.List());

        Assert.True(store.Delete(file.FileName));
        Assert.Empty(store.List());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
