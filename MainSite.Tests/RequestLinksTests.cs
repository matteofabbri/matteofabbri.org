using MainSite.RequestLogging;

namespace MainSite.Tests;

public sealed class RequestLinksTests
{
    [Theory]
    [InlineData("/blog/post", "/blog/post")]
    [InlineData("/.env", "/.env")]
    [InlineData("/requests?days=7", "/requests?days=7")]
    [InlineData("//evil.example/x", null)]
    [InlineData("/\\evil.example/x", null)]
    [InlineData("https://evil.example/x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_same_origin_paths_are_linked(string? input, string? expected)
        => Assert.Equal(expected, RequestLinks.SafeHref(input));
}
