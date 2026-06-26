using System.Text;
using MainSite.Blog;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class JumbotronMediaStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "jumbotron-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Salva_elenca_e_elimina_una_foto()
    {
        var store = CreateStore();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0];

        using var stream = new MemoryStream(png);
        var file = await store.SaveAsync("hero.png", stream, png.Length);

        Assert.False(file.IsVideo);
        Assert.StartsWith("/jumbotron/media/", file.Url);
        Assert.NotNull(store.ResolvePath(file.FileName));
        Assert.Equal("image/png", store.ContentTypeFor(file.FileName));
        Assert.Single(store.List());

        Assert.True(store.Delete(file.FileName));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Salva_un_video_mp4()
    {
        var store = CreateStore();
        byte[] mp4 =
        [
            0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0, 0, 0, 0,
        ];

        using var stream = new MemoryStream(mp4);
        var file = await store.SaveAsync("clip.mp4", stream, mp4.Length);

        Assert.True(file.IsVideo);
        Assert.Equal("video/mp4", store.ContentTypeFor(file.FileName));
        Assert.Single(store.List());
    }

    [Fact]
    public async Task Rifiuta_formati_non_supportati_e_file_troppo_grandi()
    {
        var store = CreateStore();

        using var svg = new MemoryStream(Encoding.UTF8.GetBytes("<svg/>"));
        await Assert.ThrowsAsync<BlogValidationException>(() => store.SaveAsync("x.svg", svg, svg.Length));

        using var big = new MemoryStream(new byte[16]);
        await Assert.ThrowsAsync<BlogValidationException>(() =>
            store.SaveAsync("big.png", big, MediaLibrary.MaxImageBytes + 1));
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

    private JumbotronMediaStore CreateStore() => new(
        Options.Create(new BlogOptions { ContentPath = _path }),
        new TestHostEnvironment(_path));
}
