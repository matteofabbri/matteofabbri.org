using MainSite.Blog;

namespace MainSite.Tests;

public sealed class PostMediaTests
{
    [Fact]
    public void Estrae_link_e_immagini_esterni_nell_ordine_del_documento()
    {
        const string markdown = """
            Guarda [questo video](https://www.youtube.com/watch?v=abc123) e poi
            ![una foto](https://example.com/foto.png), infine <https://vimeo.com/123>.
            """;

        var urls = PostMedia.ExtractUrls(markdown);

        Assert.Equal(
            new[]
            {
                "https://www.youtube.com/watch?v=abc123",
                "https://vimeo.com/123",
            },
            urls);
    }

    [Fact]
    public void Salta_immagini_file_locali_e_link_al_sito_stesso()
    {
        const string markdown = """
            ![foto](https://example.com/foto.jpeg)
            ![locale](/blog/media/foto.jpg)
            [post](https://matteofabbri.org/blog/altro)
            [video](https://youtu.be/xyz)
            [mail](mailto:matteo@matteofabbri.org)
            """;

        var urls = PostMedia.ExtractUrls(markdown, "matteofabbri.org");

        Assert.Equal(new[] { "https://youtu.be/xyz" }, urls);
    }

    [Fact]
    public void Toglie_i_duplicati()
    {
        const string markdown = """
            [primo](https://youtu.be/abc)
            [secondo](https://youtu.be/abc)
            https://youtu.be/abc
            """;

        var urls = PostMedia.ExtractUrls(markdown);

        Assert.Single(urls);
    }

    [Fact]
    public void Un_corpo_vuoto_non_produce_link()
    {
        Assert.Empty(PostMedia.ExtractUrls(null));
        Assert.Empty(PostMedia.ExtractUrls(""));
        Assert.Empty(PostMedia.ExtractUrls("Solo testo, nessun link."));
    }

    [Theory]
    [InlineData("https://youtu.be/abc", "https://youtu.be/abc")]
    [InlineData("HTTPS://YOUTU.BE/abc", "https://youtu.be/abc")]
    [InlineData("https://youtu.be/abc?t=10", "https://youtu.be/abc?t=10")]
    public void Normalizza_lo_schema_e_riconosce_l_host(string input, string expected)
    {
        var urls = PostMedia.ExtractUrls($"[video]({input})", "matteofabbri.org");

        Assert.Equal(new[] { expected }, urls);
    }
}
