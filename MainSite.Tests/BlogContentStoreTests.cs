using MainSite.Blog;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class BlogContentStoreTests : IDisposable
{
    private static readonly byte[] Mp4 =
    [
        0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0, 0, 0, 0,
    ];

    private readonly string _path = Path.Combine(Path.GetTempPath(), "blog-content-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Copia_i_media_di_content_in_404_e_lascia_il_deposito_intatto()
    {
        var content = CreateContentStore();
        var notFound = CreateNotFoundStore();

        File.WriteAllBytes(Path.Combine(content.DirectoryPath, "clip.mp4"), Mp4);
        File.WriteAllText(Path.Combine(content.DirectoryPath, "note.txt"), "non è un media");

        var result = await content.CopyToAsync(notFound);

        Assert.Equal(1, result.Copied);
        Assert.Single(result.Skipped);
        Assert.Contains("note.txt", result.Skipped[0]);
        Assert.NotNull(notFound.ResolvePath("clip.mp4"));
        Assert.True(File.Exists(Path.Combine(content.DirectoryPath, "clip.mp4")));
    }

    [Fact]
    public async Task Rieseguire_la_copia_non_duplica_i_file()
    {
        var content = CreateContentStore();
        var notFound = CreateNotFoundStore();

        File.WriteAllBytes(Path.Combine(content.DirectoryPath, "clip.mp4"), Mp4);

        var first = await content.CopyToAsync(notFound);
        var second = await content.CopyToAsync(notFound);

        Assert.Equal(1, first.Copied);
        Assert.Equal(0, second.Copied);
        Assert.Single(second.Skipped);
        Assert.Contains("already in 404", second.Skipped[0]);
        Assert.Single(notFound.List());
    }

    [Fact]
    public void Elenca_i_file_ma_non_i_download_a_meta()
    {
        var content = CreateContentStore();
        File.WriteAllBytes(Path.Combine(content.DirectoryPath, "clip.mp4"), Mp4);
        File.WriteAllText(Path.Combine(content.DirectoryPath, "clip.mp4.part"), "in corso");

        var files = content.List();

        Assert.Single(files);
        Assert.Equal("clip.mp4", files[0].FileName);
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

    private BlogContentStore CreateContentStore() => new(
        Options.Create(new BlogOptions { ContentPath = _path }),
        new TestHostEnvironment(_path));

    private NotFoundMediaStore CreateNotFoundStore() => new(
        Options.Create(new BlogOptions { ContentPath = _path }),
        new TestHostEnvironment(_path));
}
