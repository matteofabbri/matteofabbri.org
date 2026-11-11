namespace MainSite.Analytics;

// Umami tracker configuration (section "Umami" of appsettings.json). Empty
// ScriptUrl or WebsiteId means the tracker is not rendered at all.
public sealed class UmamiOptions
{
    public const string Section = "Umami";

    // Absolute URL of the tracker script served by the Umami instance.
    public string ScriptUrl { get; set; } = "";

    // Website id created in the Umami dashboard.
    public string WebsiteId { get; set; } = "";

    public bool IsConfigured =>
        Uri.TryCreate(ScriptUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrWhiteSpace(WebsiteId);
}
