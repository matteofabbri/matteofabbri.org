using MainSite.Analytics;

namespace MainSite.Tests;

public sealed class UmamiOptionsTests
{
    [Fact]
    public void Configured_requires_a_valid_url_and_a_website_id()
    {
        var options = new UmamiOptions
        {
            ScriptUrl = "https://analytics.example.com/script.js",
            WebsiteId = "94db1cb1-74f4-4a40-ad6c-962362670409",
        };

        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void Tracker_is_off_without_url_or_website_id()
    {
        Assert.False(new UmamiOptions().IsConfigured);
        Assert.False(new UmamiOptions { ScriptUrl = "https://analytics.example.com/script.js" }.IsConfigured);
        Assert.False(new UmamiOptions { WebsiteId = "abc" }.IsConfigured);
        Assert.False(new UmamiOptions { ScriptUrl = "not a url", WebsiteId = "abc" }.IsConfigured);
    }
}
