using MainSite.Blog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

// The blog without a shared disk and without any external provider: every test
// gets its own temporary folder, deleted at the end.
public sealed class BlogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mfsite-blog-tests", Guid.NewGuid().ToString("N"));

    private BlogStore CreateStore(string? contentPath = null)
    {
        var options = new BlogOptions
        {
            ContentPath = contentPath ?? _root,
            TimeZone = "Europe/Rome",
        };

        var environment = new TestHostEnvironment(_root);
        var clock = new BlogClock(Options.Create(options), NullLogger<BlogClock>.Instance);
        return new BlogStore(Options.Create(options), environment, clock, NullLogger<BlogStore>.Instance);
    }

    [Fact]
    public async Task Salvare_e_rileggere_un_articolo_conserva_tutti_i_metadati()
    {
        var store = CreateStore();
        var published = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.FromHours(2));

        var saved = await store.SaveAsync(new BlogPostInput
        {
            Slug = "perche-scrivo",
            Title = "Perché scrivo",
            Summary = "Una nota breve.",
            Markdown = "# Ciao\n\nTesto con **grassetto**.",
            Tags = ["note", "personale"],
            Published = published,
            Draft = false,
            Author = "matteo@maddalena.me",
        });

        Assert.Equal("perche-scrivo", saved.Slug);
        Assert.True(File.Exists(Path.Combine(_root, "perche-scrivo.md")));

        var read = await store.GetAsync("perche-scrivo");
        Assert.NotNull(read);
        Assert.Equal("Perché scrivo", read!.Title);
        Assert.Equal("Una nota breve.", read.Summary);
        Assert.Contains("**grassetto**", read.Markdown);
        Assert.Equal(new[] { "note", "personale" }, read.Tags);
        Assert.Equal(published, read.Published);
        Assert.False(read.Draft);
        Assert.Equal("matteo@maddalena.me", read.Author);
    }

    [Fact]
    public async Task Un_titolo_con_accenti_produce_uno_slug_ascii()
    {
        var store = CreateStore();

        var saved = await store.SaveAsync(new BlogPostInput
        {
            Slug = BlogMarkdown.Slugify("Perché scrivo di città e d'estate"),
            Title = "Perché scrivo di città e d'estate",
            Markdown = "Testo.",
            Published = DateTimeOffset.UtcNow,
            Author = "matteo@maddalena.me",
        });

        Assert.Equal("perche-scrivo-di-citta-e-d-estate", saved.Slug);
    }

    [Fact]
    public async Task Un_titolo_con_caratteri_speciali_sopravvive_al_front_matter()
    {
        var store = CreateStore();

        var saved = await store.SaveAsync(new BlogPostInput
        {
            Slug = "caratteri-speciali",
            Title = "C++ e C#: due lingue — perché no?",
            Summary = "Virgolette \"doppie\", due punti: e un trattino — tutto qui.",
            Markdown = "Testo.",
            Published = DateTimeOffset.UtcNow,
            Tags = ["c++", "c#"],
            Author = "matteo@maddalena.me",
        });

        var read = await store.GetAsync("caratteri-speciali");

        Assert.NotNull(read);
        Assert.Equal(saved.Title, read!.Title);
        Assert.Equal(saved.Summary, read.Summary);
        Assert.Equal(new[] { "c++", "c#" }, read.Tags);
    }

    [Fact]
    public async Task Due_articoli_non_possono_avere_lo_stesso_slug()
    {
        var store = CreateStore();
        await Save(store, "uguale", "Primo");

        await Assert.ThrowsAsync<BlogValidationException>(() => Save(store, "uguale", "Secondo"));
    }

    [Fact]
    public async Task Cambiare_slug_scrive_il_nuovo_file_e_sposta_il_vecchio_nel_cestino()
    {
        var store = CreateStore();
        await Save(store, "vecchio-slug", "Titolo");

        var saved = await store.SaveAsync(new BlogPostInput
        {
            Slug = "nuovo-slug",
            Title = "Titolo aggiornato",
            Markdown = "Testo nuovo.",
            Published = DateTimeOffset.UtcNow,
            Author = "matteo@maddalena.me",
        }, originalSlug: "vecchio-slug");

        Assert.Equal("nuovo-slug", saved.Slug);
        Assert.NotNull(await store.GetAsync("nuovo-slug"));
        Assert.Null(await store.GetAsync("vecchio-slug"));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "_trash"), "*vecchio-slug.md"));
    }

    [Fact]
    public async Task Eliminare_un_articolo_lo_sposta_nel_cestino()
    {
        var store = CreateStore();
        await Save(store, "da-cancellare", "Da cancellare");

        Assert.True(await store.DeleteAsync("da-cancellare"));
        Assert.Null(await store.GetAsync("da-cancellare"));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "_trash"), "*da-cancellare.md"));
    }

    [Fact]
    public async Task Un_file_senza_front_matter_usa_il_primo_titolo_e_resta_leggibile()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "scritto-a-mano.md"),
            "# Un titolo\n\nTesto scritto direttamente nel file.\n");

        var store = CreateStore();
        var post = await store.GetAsync("scritto-a-mano");

        Assert.NotNull(post);
        Assert.Equal("Un titolo", post!.Title);
        Assert.Contains("direttamente", post.Markdown);
        Assert.NotEqual(default, post.Published);
    }

    [Fact]
    public async Task Un_front_matter_rotto_non_pubblica_l_articolo()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "rotto.md"),
            "---\ntitle: [non chiuso\n---\n\nTesto.\n");

        var store = CreateStore();

        Assert.Null(await store.GetAsync("rotto"));
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task Le_date_del_front_matter_si_interpretano_nel_fuso_del_blog()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "estate.md"),
            "---\ntitle: Estate\npublished: 2026-07-01\n---\n\nTesto.\n");

        var store = CreateStore();
        var post = await store.GetAsync("estate");

        Assert.NotNull(post);
        Assert.Equal(TimeSpan.FromHours(2), post!.Published.Offset);
    }

    [Fact]
    public async Task Un_orario_senza_offset_si_interpreta_nel_fuso_del_blog()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "senza-offset.md"),
            "---\ntitle: Senza offset\npublished: 2026-07-01T09:00:00\n---\n\nTesto.\n");

        var store = CreateStore();
        var post = await store.GetAsync("senza-offset");

        Assert.NotNull(post);
        Assert.Equal(TimeSpan.FromHours(2), post!.Published.Offset);
        Assert.Equal(9, post.Published.Hour);
    }

    [Fact]
    public async Task L_elenco_e_ordinato_dal_piu_recente_e_include_le_bozze()
    {
        var store = CreateStore();
        await Save(store, "vecchio", "Vecchio", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await Save(store, "nuovo", "Nuovo", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), draft: true);

        var posts = await store.GetAllAsync();

        Assert.Collection(posts,
            first => Assert.Equal("nuovo", first.Slug),
            second => Assert.Equal("vecchio", second.Slug));
        Assert.True(posts[0].Draft);
    }

    [Fact]
    public async Task Uno_slug_gia_usato_viene_reso_unico_solo_quando_lo_chiede_l_editor()
    {
        var store = CreateStore();
        await Save(store, "post", "Primo");

        Assert.Equal("post-2", store.UniqueSlug("post"));
        Assert.False(store.SlugExists("post-2"));
    }

    [Fact]
    public async Task Una_data_futura_o_una_bozza_non_sono_visibili()
    {
        var store = CreateStore();
        var future = DateTimeOffset.UtcNow.AddDays(2);

        await Save(store, "programmato", "Programmato", future);
        await Save(store, "bozza", "Bozza", DateTimeOffset.UtcNow.AddDays(-1), draft: true);

        var programmato = await store.GetAsync("programmato");
        var bozza = await store.GetAsync("bozza");

        Assert.False(programmato!.IsVisibleAt(DateTimeOffset.UtcNow));
        Assert.False(bozza!.IsVisibleAt(DateTimeOffset.UtcNow));
        Assert.True(programmato.IsVisibleAt(future.AddMinutes(1)));
    }

    private static Task<BlogPost> Save(
        BlogStore store, string slug, string title,
        DateTimeOffset? published = null, bool draft = false) =>
        store.SaveAsync(new BlogPostInput
        {
            Slug = slug,
            Title = title,
            Markdown = "Testo dell'articolo.",
            Published = published ?? DateTimeOffset.UtcNow,
            Draft = draft,
            Author = "matteo@maddalena.me",
        });

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
