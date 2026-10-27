namespace MainSite.RequestLogging;

// Risultato della lettura "best effort" dello User-Agent.
public readonly record struct UserAgentInfo(
    string Browser,
    string BrowserVersion,
    string OperatingSystem,
    string OperatingSystemVersion,
    string DeviceType,
    bool IsBot)
{
    public static readonly UserAgentInfo Unknown =
        new("Unknown", "", "Unknown", "", "unknown", false);
}

// Parser minimale, senza dipendenze esterne: serve a rispondere alle domande
// che si fanno davvero sui log (quale browser, quale sistema, mobile o
// desktop, è un bot?), non a ricostruire ogni variante esistente.
public static class UserAgentParser
{
    private static readonly (string Token, string Name)[] Bots =
    [
        // Motori di ricerca
        ("Googlebot/", "Googlebot"), ("Google-InspectionTool/", "Google Inspection Tool"),
        ("Storebot-Google/", "Google Storebot"), ("GoogleOther/", "GoogleOther"),
        ("bingbot/", "Bingbot"), ("BingPreview/", "BingPreview"), ("msnbot/", "MSN Bot"),
        ("DuckDuckBot/", "DuckDuckBot"),
        ("YandexBot/", "YandexBot"), ("YandexImages/", "YandexImages"),
        ("Baiduspider", "Baiduspider"), ("Sogou", "Sogou Spider"),
        ("SeznamBot/", "SeznamBot"), ("Qwantify/", "Qwantify"),
        ("Applebot/", "Applebot"), ("PetalBot", "PetalBot"), ("Bytespider", "Bytespider"),

        // SEO / analytics
        ("AhrefsBot/", "AhrefsBot"), ("SemrushBot/", "SemrushBot"), ("MJ12bot/", "MJ12bot"),
        ("DotBot/", "DotBot"), ("rogerbot/", "Rogerbot"), ("Screaming Frog SEO Spider/", "Screaming Frog"),

        // AI / crawler di modelli
        ("GPTBot/", "GPTBot"), ("ChatGPT-User/", "ChatGPT-User"), ("OAI-SearchBot/", "OAI SearchBot"),
        ("ClaudeBot/", "ClaudeBot"), ("Claude-Web/", "Claude-Web"), ("anthropic-ai/", "Anthropic AI"),
        ("PerplexityBot/", "PerplexityBot"), ("CCBot/", "Common Crawl"), ("cohere-ai", "Cohere AI"),
        ("Meta-ExternalAgent/", "Meta External Agent"), ("FacebookBot/", "FacebookBot"),

        // Social / anteprime link
        ("Twitterbot/", "Twitterbot"), ("LinkedInBot/", "LinkedInBot"),
        ("TelegramBot", "TelegramBot"), ("WhatsApp/", "WhatsApp"),
        ("Discordbot/", "Discordbot"), ("Slackbot", "Slackbot"),
        ("Pinterestbot/", "Pinterestbot"), ("redditbot/", "RedditBot"),
        ("facebookexternalhit/", "Facebook"), ("Facebot/", "Facebook"),

        // Client da riga di comando, librerie HTTP, automazione
        ("curl/", "curl"), ("Wget/", "Wget"), ("python-requests/", "python-requests"),
        ("python-urllib/", "python-urllib"), ("aiohttp/", "aiohttp"),
        ("Go-http-client/", "Go-http-client"), ("okhttp/", "OkHttp"),
        ("Apache-HttpClient/", "Apache HttpClient"), ("Java/", "Java"),
        ("libwww-perl/", "libwww-perl"), ("PostmanRuntime/", "Postman"),
        ("insomnia/", "Insomnia"), ("HeadlessChrome/", "Headless Chrome"),
        ("PhantomJS/", "PhantomJS"), ("Lighthouse/", "Lighthouse"),
        ("Puppeteer/", "Puppeteer"), ("Playwright/", "Playwright"), ("Selenium/", "Selenium"),

        // Monitoraggio / uptime
        ("UptimeRobot/", "UptimeRobot"), ("Pingdom", "Pingdom"), ("StatusCake", "StatusCake"),
        ("Datadog/", "Datadog"), ("NewRelicPinger/", "New Relic"), ("Better Uptime", "Better Uptime"),

        // Parole generiche (ultime: meno precise). "bot/" copre i crawler
        // meno noti senza scambiare per bot un modello di telefono (CUBOT X30
        // non contiene "bot/").
        ("bot/", "Bot"), ("crawler", "Crawler"), ("spider", "Spider"), ("slurp", "Slurp"),
    ];

    public static UserAgentInfo Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return UserAgentInfo.Unknown;

        foreach (var (token, name) in Bots)
        {
            int at = userAgent.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var botOs = OperatingSystemOf(userAgent);
                return new UserAgentInfo(
                    name,
                    VersionAt(userAgent, at + token.Length),
                    botOs.Name,
                    botOs.Version,
                    "bot",
                    true);
            }
        }

        var (browser, browserVersion) = BrowserOf(userAgent);
        var osInfo = OperatingSystemOf(userAgent);

        return new UserAgentInfo(browser, browserVersion, osInfo.Name, osInfo.Version, DeviceOf(userAgent), false);
    }

    // L'ordine conta: Edge e Opera contengono "Chrome/", Firefox iOS contiene
    // "Safari/", e così via. Si controllano prima i più specifici.
    private static (string Browser, string Version) BrowserOf(string ua)
    {
        if (Has(ua, "Edg/") || Has(ua, "EdgA/") || Has(ua, "EdgiOS/") || Has(ua, "Edge/"))
        {
            string marker = Has(ua, "EdgA/") ? "EdgA/"
                : Has(ua, "EdgiOS/") ? "EdgiOS/"
                : Has(ua, "Edge/") ? "Edge/"
                : "Edg/";
            return ("Edge", VersionAfter(ua, marker));
        }
        if (Has(ua, "OPR/") || Has(ua, "OPiOS/") || Has(ua, "Opera/"))
            return ("Opera", VersionAfter(ua, Has(ua, "OPR/") ? "OPR/" : Has(ua, "OPiOS/") ? "OPiOS/" : "Opera/"));
        if (Has(ua, "SamsungBrowser/"))
            return ("Samsung Internet", VersionAfter(ua, "SamsungBrowser/"));
        if (Has(ua, "Vivaldi/"))
            return ("Vivaldi", VersionAfter(ua, "Vivaldi/"));
        if (Has(ua, "Brave/"))
            return ("Brave", VersionAfter(ua, "Brave/"));
        if (Has(ua, "DuckDuckGo/"))
            return ("DuckDuckGo", VersionAfter(ua, "DuckDuckGo/"));
        if (Has(ua, "Firefox/") || Has(ua, "FxiOS/"))
            return ("Firefox", VersionAfter(ua, Has(ua, "FxiOS/") ? "FxiOS/" : "Firefox/"));
        if (Has(ua, "HeadlessChrome/"))
            return ("Chrome Headless", VersionAfter(ua, "HeadlessChrome/"));
        if (Has(ua, "CriOS/") || Has(ua, "Chrome/"))
            return ("Chrome", VersionAfter(ua, Has(ua, "CriOS/") ? "CriOS/" : "Chrome/"));
        if (Has(ua, "Version/") && Has(ua, "Safari/"))
            return ("Safari", VersionAfter(ua, "Version/"));
        if (Has(ua, "MSIE "))
            return ("Internet Explorer", VersionAfter(ua, "MSIE "));
        if (Has(ua, "Trident/"))
            return ("Internet Explorer", VersionAfter(ua, "rv:"));

        return ("Other", "");
    }

    private static (string Name, string Version) OperatingSystemOf(string ua)
    {
        if (Has(ua, "Windows Phone"))
            return ("Windows Phone", VersionAfter(ua, "Windows Phone "));
        if (Has(ua, "Windows NT "))
        {
            string version = VersionAfter(ua, "Windows NT ");
            string name = version switch
            {
                "10.0" => "Windows 10/11",
                "6.3" => "Windows 8.1",
                "6.2" => "Windows 8",
                "6.1" => "Windows 7",
                "6.0" => "Windows Vista",
                "5.1" => "Windows XP",
                _ => "Windows",
            };
            return (name, version);
        }

        // iOS prima di macOS: l'UA di iPhone/iPad contiene "like Mac OS X".
        if (Has(ua, "iPhone") || Has(ua, "iPad") || Has(ua, "iPod"))
        {
            string version = VersionAfter(ua, Has(ua, "CPU OS ") ? "CPU OS " : "iPhone OS ");
            return ("iOS", version);
        }
        if (Has(ua, "Android "))
            return ("Android", VersionAfter(ua, "Android "));
        if (Has(ua, "CrOS "))
            return ("ChromeOS", "");
        if (Has(ua, "Mac OS X "))
            return ("macOS", VersionAfter(ua, "Mac OS X "));
        if (Has(ua, "Ubuntu"))
            return ("Ubuntu", "");
        if (Has(ua, "Fedora"))
            return ("Fedora", "");
        if (Has(ua, "FreeBSD"))
            return ("FreeBSD", "");
        if (Has(ua, "OpenBSD"))
            return ("OpenBSD", "");
        if (Has(ua, "Linux"))
            return ("Linux", "");

        return ("Unknown", "");
    }

    private static string DeviceOf(string ua)
    {
        if (Has(ua, "Windows Phone"))
            return "mobile";
        if (Has(ua, "iPad") || Has(ua, "Tablet") || (Has(ua, "Android") && !Has(ua, "Mobile")))
            return "tablet";
        if (Has(ua, "Mobi") || Has(ua, "iPhone") || Has(ua, "iPod"))
            return "mobile";
        return "desktop";
    }

    private static bool Has(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string VersionAfter(string ua, string marker)
    {
        int at = ua.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? "" : VersionAt(ua, at + marker.Length);
    }

    private static string VersionAt(string ua, int start)
    {
        int end = start;
        while (end < ua.Length && (char.IsAsciiDigit(ua[end]) || ua[end] is '.' or '_'))
            end++;
        return ua[start..end].Replace('_', '.');
    }
}
