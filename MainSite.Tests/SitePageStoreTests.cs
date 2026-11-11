using MainSite.Blog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class SitePageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mfsite-page-tests", Guid.NewGuid().ToString("N"));

    private SitePageStore CreateStore() => new(
        Options.Create(new BlogOptions { ContentPath = _root }),
        new TestHostEnvironment(_root),
        NullLogger<SitePageStore>.Instance);

    [Fact]
    public async Task Missing_page_returns_the_built_in_about_page()
    {
        var page = await CreateStore().GetAsync(AboutPage.Slug);

        Assert.Equal(AboutPage.DefaultTitle, page.Title);
        Assert.Equal(AboutPage.DefaultDescription, page.Description);
        Assert.Contains("One AppHost", page.Markdown);
    }

    [Fact]
    public async Task Save_and_read_round_trip()
    {
        var store = CreateStore();

        await store.SaveAsync(
            AboutPage.Slug,
            "A: title with a colon",
            "A lede with \"quotes\": and a colon.",
            "# Body\n\nText with **markdown**.");

        Assert.True(File.Exists(Path.Combine(_root, "pages", "about.md")));

        var page = await store.GetAsync(AboutPage.Slug);
        Assert.Equal("A: title with a colon", page.Title);
        Assert.Equal("A lede with \"quotes\": and a colon.", page.Description);
        Assert.Equal("# Body\n\nText with **markdown**.", page.Markdown);
    }

    [Fact]
    public async Task Seed_writes_the_file_and_does_not_overwrite_later_edits()
    {
        var store = CreateStore();
        await store.EnsureSeededAsync();
        Assert.True(File.Exists(Path.Combine(_root, "pages", "about.md")));

        await store.SaveAsync(AboutPage.Slug, "Edited", "Edited lede", "Edited body.");
        await CreateStore().EnsureSeededAsync();

        var page = await store.GetAsync(AboutPage.Slug);
        Assert.Equal("Edited", page.Title);
        Assert.Equal("Edited body.", page.Markdown);
    }

    [Fact]
    public async Task Unknown_page_is_rejected()
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<BlogValidationException>(() => store.GetAsync("not-a-page"));
        await Assert.ThrowsAsync<BlogValidationException>(
            () => store.SaveAsync("not-a-page", "Title", "", "Body"));
    }

    [Fact]
    public async Task Malformed_front_matter_falls_back_to_the_built_in_copy()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pages"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "pages", "about.md"),
            "---\ntitle: [not closed\n---\n\nBody.\n");

        var page = await CreateStore().GetAsync(AboutPage.Slug);

        Assert.Equal(AboutPage.DefaultTitle, page.Title);
        Assert.Contains("One AppHost", page.Markdown);
    }

    [Fact]
    public async Task Empty_body_falls_back_to_the_built_in_markdown()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pages"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "pages", "about.md"),
            "---\ntitle: Custom title\ndescription: Custom lede\n---\n");

        var page = await CreateStore().GetAsync(AboutPage.Slug);

        Assert.Equal("Custom title", page.Title);
        Assert.Equal("Custom lede", page.Description);
        Assert.Contains("One AppHost", page.Markdown);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
