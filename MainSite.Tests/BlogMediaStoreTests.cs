using MainSite.Blog;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class BlogMediaStoreTests : IDisposable
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mfsite-blog-media-tests", Guid.NewGuid().ToString("N"));

    private BlogMediaStore CreateStore() =>
        new(Options.Create(new BlogOptions { ContentPath = _root }), new TestHostEnvironment(_root));

    [Fact]
    public async Task Un_png_valido_viene_salvato_e_servito_dal_percorso_pubblico()
    {
        var store = CreateStore();

        var saved = await store.SaveAsync("foto.png", new MemoryStream(TinyPng), TinyPng.Length);

        Assert.EndsWith(".png", saved.FileName);
        Assert.StartsWith("/blog/media/", saved.Url);
        Assert.Equal("image/png", store.ContentTypeFor(saved.FileName));
        Assert.NotNull(store.ResolvePath(saved.FileName));
        Assert.Contains(saved.FileName, store.List().Select(f => f.FileName));
    }

    [Fact]
    public async Task Un_file_che_non_e_un_immagine_viene_rifiutato_anche_se_ha_l_estensione_giusta()
    {
        var store = CreateStore();
        byte[] fake = "non sono un png"u8.ToArray();

        await Assert.ThrowsAsync<BlogValidationException>(
            () => store.SaveAsync("finta.png", new MemoryStream(fake), fake.Length));
    }

    [Fact]
    public async Task Gli_svg_sono_esclusi()
    {
        var store = CreateStore();
        byte[] svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();

        await Assert.ThrowsAsync<BlogValidationException>(
            () => store.SaveAsync("logo.svg", new MemoryStream(svg), svg.Length));
    }

    [Fact]
    public async Task Un_nome_con_percorso_non_esce_dalla_cartella_media()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync("foto.png", new MemoryStream(TinyPng), TinyPng.Length);

        Assert.Null(store.ResolvePath("../" + saved.FileName));
        Assert.Null(store.ResolvePath("..\\" + saved.FileName));
        Assert.Null(store.ResolvePath("manca.png"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
