using MainSite.RequestLogging;

namespace MainSite.Tests;

// Il parser non vuole essere un motore di user-agent completo: questi casi
// coprono i client che passano davvero da un sito personale, più i bot.
public sealed class UserAgentParserTests
{
    [Theory]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
        "Chrome", "126.0.0.0", "Windows 10/11", "10.0", "desktop")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0",
        "Edge", "126.0.0.0", "Windows 10/11", "10.0", "desktop")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36 Edge/18.19041",
        "Edge", "18.19041", "Windows 10/11", "10.0", "desktop")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 DuckDuckGo/7",
        "DuckDuckGo", "7", "Windows 10/11", "10.0", "desktop")]
    [InlineData(
        "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:127.0) Gecko/20100101 Firefox/127.0",
        "Firefox", "127.0", "Ubuntu", "", "desktop")]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
        "Safari", "17.5", "iOS", "17.5", "mobile")]
    [InlineData(
        "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
        "Safari", "17.5", "iOS", "17.5", "tablet")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36",
        "Chrome", "126.0.0.0", "Android", "14", "mobile")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
        "Chrome", "126.0.0.0", "Android", "14", "tablet")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/25.0 Chrome/121.0.0.0 Mobile Safari/537.36",
        "Samsung Internet", "25.0", "Android", "14", "mobile")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Vivaldi/6.7",
        "Vivaldi", "6.7", "Windows 10/11", "10.0", "desktop")]
    public void Riconosce_browser_sistema_e_dispositivo(
        string userAgent,
        string browser,
        string browserVersion,
        string operatingSystem,
        string operatingSystemVersion,
        string deviceType)
    {
        var info = UserAgentParser.Parse(userAgent);

        Assert.Equal(browser, info.Browser);
        Assert.Equal(browserVersion, info.BrowserVersion);
        Assert.Equal(operatingSystem, info.OperatingSystem);
        Assert.Equal(operatingSystemVersion, info.OperatingSystemVersion);
        Assert.Equal(deviceType, info.DeviceType);
        Assert.False(info.IsBot);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "Googlebot", "2.1")]
    [InlineData("Mozilla/5.0 (compatible; GPTBot/1.0; +https://openai.com/gptbot)", "GPTBot", "1.0")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)", "Bingbot", "2.0")]
    [InlineData("curl/8.4.0", "curl", "8.4.0")]
    [InlineData("python-requests/2.31.0", "python-requests", "2.31.0")]
    [InlineData("MyBot/1.0", "Bot", "1.0")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/126.0.0.0 Safari/537.36", "Headless Chrome", "126.0.0.0")]
    public void Riconosce_i_bot_e_gli_automi(string userAgent, string browser, string browserVersion)
    {
        var info = UserAgentParser.Parse(userAgent);

        Assert.True(info.IsBot);
        Assert.Equal("bot", info.DeviceType);
        Assert.Equal(browser, info.Browser);
        Assert.Equal(browserVersion, info.BrowserVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Uno_user_agent_vuoto_non_e_un_bot(string? userAgent)
    {
        var info = UserAgentParser.Parse(userAgent);

        Assert.Equal(UserAgentInfo.Unknown, info);
        Assert.False(info.IsBot);
    }

    [Fact]
    public void Un_client_sconosciuto_resta_altro_senza_inventare_un_bot()
    {
        var info = UserAgentParser.Parse("MyCustomClient/1.0");

        Assert.Equal("Other", info.Browser);
        Assert.Equal("Unknown", info.OperatingSystem);
        Assert.Equal("desktop", info.DeviceType);
        Assert.False(info.IsBot);
    }

    [Fact]
    public void Un_telefono_con_la_parola_bot_nel_modello_non_viene_scambiato_per_un_bot()
    {
        // "CUBOT" è un marchio di telefoni: il match non deve essere sulla
        // sottostringa "bot" nuda.
        var info = UserAgentParser.Parse(
            "Mozilla/5.0 (Linux; Android 13; CUBOT X30) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36");

        Assert.False(info.IsBot);
        Assert.Equal("mobile", info.DeviceType);
        Assert.Equal("Chrome", info.Browser);
    }

    [Fact]
    public void L_etichetta_del_client_e_compatta_e_leggibile()
    {
        Assert.Equal(
            "Chrome 126.0.0.0 · Windows 10/11",
            UserAgentParser.Parse("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36").ClientLabel);

        Assert.Equal(
            "Googlebot 2.1",
            UserAgentParser.Parse("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)").ClientLabel);
    }
}
