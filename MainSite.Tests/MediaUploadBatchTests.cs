using MainSite.Blog;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class MediaUploadBatchTests : IDisposable
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly string _path = Path.Combine(Path.GetTempPath(), "upload-batch-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Saves_every_valid_file_in_the_batch()
    {
        var store = CreateStore();
        var files = new[]
        {
            Upload("one.png", Png),
            Upload("two.png", Png),
            Upload("three.png", Png),
        };

        var result = await SaveAsync(store, files);

        Assert.Empty(result.Failed);
        Assert.Equal(3, result.Saved.Count);
        Assert.Equal(3, store.List().Count);
    }

    [Fact]
    public async Task One_invalid_file_does_not_stop_the_others()
    {
        var store = CreateStore();
        var files = new[]
        {
            Upload("good-one.png", Png),
            Upload("bad.svg", "<svg/>"u8.ToArray()),
            Upload("good-two.png", Png),
        };

        var result = await SaveAsync(store, files);

        Assert.Equal(2, result.Saved.Count);
        Assert.Single(result.Failed);
        Assert.Contains("bad.svg", result.Failed[0]);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public async Task An_empty_file_is_reported_without_reaching_the_store()
    {
        var store = CreateStore();
        var files = new[] { Upload("empty.png", []) };

        var result = await SaveAsync(store, files);

        Assert.Empty(result.Saved);
        Assert.Single(result.Failed);
        Assert.Contains("empty.png", result.Failed[0]);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Report_sets_one_success_and_one_error_message()
    {
        var tempData = CreateTempData();

        MediaUploadBatch.Report(tempData, new MediaUploadBatch.Result(
            ["a.png", "b.png"],
            ["c.svg: Unsupported format."]));

        Assert.Equal("Uploaded 2 files.", tempData["Messaggio"]);
        Assert.Equal("c.svg: Unsupported format.", tempData["Errore"]);
    }

    [Fact]
    public void Report_shortens_a_long_failure_list()
    {
        var tempData = CreateTempData();

        MediaUploadBatch.Report(tempData, new MediaUploadBatch.Result(
            [],
            ["a: bad", "b: bad", "c: bad", "d: bad", "e: bad"]));

        Assert.Null(tempData["Messaggio"]);
        Assert.Equal("a: bad b: bad c: bad and 2 more.", tempData["Errore"]);
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

    private static Task<MediaUploadBatch.Result> SaveAsync(JumbotronMediaStore store, IFormFile[] files) =>
        MediaUploadBatch.SaveAsync(
            files,
            async (name, stream, length, ct) => (await store.SaveAsync(name, stream, length, ct)).FileName,
            CancellationToken.None);

    private JumbotronMediaStore CreateStore() => new(
        Options.Create(new BlogOptions { ContentPath = _path }),
        new TestHostEnvironment(_path));

    private static FormFile Upload(string name, byte[] bytes) =>
        new(new MemoryStream(bytes), 0, bytes.Length, "Upload", name);

    private static TempDataDictionary CreateTempData() =>
        new(new DefaultHttpContext(), new MemoryTempDataProvider());

    private sealed class MemoryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
