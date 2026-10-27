using MainSite.Blog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class BlogMarkdownTests
{
    [Theory]
    [InlineData("Perché scrivo di città", "perche-scrivo-di-citta")]
    [InlineData("  Spazi   doppi  ", "spazi-doppi")]
    [InlineData("C++ e C#: due lingue", "c-e-c-due-lingue")]
    [InlineData("", "post")]
    [InlineData("!!!", "post")]
    public void Lo_slug_e_ascii_e_stabile(string input, string expected)
    {
        Assert.Equal(expected, BlogMarkdown.Slugify(input));
    }

    [Fact]
    public void Uno_slug_lungo_viene_tagliato_senza_finire_col_trattino()
    {
        string slug = BlogMarkdown.Slugify(new string('a', 120));

        Assert.Equal(80, slug.Length);
        Assert.False(slug.EndsWith('-'));
    }

    [Theory]
    [InlineData("perche-scrivo", true)]
    [InlineData("perche--scrivo", false)]
    [InlineData("Perche-Scrivo", false)]
    [InlineData("../fuori", false)]
    [InlineData("", false)]
    public void Lo_slug_deve_essere_una_parola_sicura(string slug, bool valid)
    {
        Assert.Equal(valid, BlogMarkdown.IsValidSlug(slug));
    }

    [Fact]
    public void Il_riepilogo_toglie_il_markup_e_non_taglia_a_meta_parola()
    {
        string excerpt = BlogMarkdown.Excerpt("# Titolo\n\nTesto **forte** con [un link](https://example.com).", 80);

        Assert.DoesNotContain("**", excerpt);
        Assert.DoesNotContain("](#", excerpt);
        Assert.DoesNotContain("#", excerpt);
        Assert.DoesNotContain("…", excerpt);
    }

    [Fact]
    public void Un_testo_lungo_viene_tagliato_con_i_puntini()
    {
        string excerpt = BlogMarkdown.Excerpt(new string('a', 100) + " fine", 40);

        Assert.EndsWith("…", excerpt);
        Assert.True(excerpt.Length <= 41);
    }

    [Fact]
    public void I_minuti_di_lettura_sono_almeno_uno_per_un_articolo()
    {
        string body = string.Join(' ', Enumerable.Repeat("parola", 450));

        Assert.Equal(3, BlogMarkdown.ReadingMinutes(body));
        Assert.Equal(1, BlogMarkdown.ReadingMinutes("poche parole"));
        Assert.Equal(0, BlogMarkdown.ReadingMinutes(""));
    }

    [Fact]
    public void L_html_grezzo_non_viene_eseguito()
    {
        string html = BlogMarkdown.ToHtml("Testo <script>alert('x')</script> e <b>grassetto</b>.");

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Il_markdown_produce_gli_elementi_attesi()
    {
        string html = BlogMarkdown.ToHtml("## Titolo\n\n- uno\n- due\n\n```csharp\nvar x = 1;\n```");

        Assert.Contains("<h2", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<pre><code", html);
    }

    [Fact]
    public void Le_date_rispettano_l_ora_legale_italiana()
    {
        var clock = new BlogClock(
            Options.Create(new BlogOptions { TimeZone = "Europe/Rome" }),
            NullLogger<BlogClock>.Instance);

        Assert.Equal(TimeSpan.FromHours(2), clock.ToInstant(new DateTime(2026, 7, 1, 9, 0, 0)).Offset);
        Assert.Equal(TimeSpan.FromHours(1), clock.ToInstant(new DateTime(2026, 12, 1, 9, 0, 0)).Offset);
        Assert.Equal("1 luglio 2026", clock.Format(clock.ToInstant(new DateTime(2026, 7, 1, 9, 0, 0))));
    }
}
