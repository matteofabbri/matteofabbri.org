namespace MainSite.Pages;

// L'area documentaria di /predictive-coding: le pagine in ordine di lettura,
// condivise dalla navigazione a schede e dal piè di pagina precedente/successivo.
// Le chiavi corrispondono a ViewData["DocsKey"] su ogni pagina dell'area.
public static class PredictiveCodingDocsArea
{
    public sealed record DocsPage(string Key, string Href, string Label);

    public static readonly IReadOnlyList<DocsPage> Pages = new[]
    {
        new DocsPage("overview", "/predictive-coding", "Overview"),
        new DocsPage("theory", "/predictive-coding/theory", "The theory"),
        new DocsPage("architectures", "/predictive-coding/architectures", "Architectures"),
        new DocsPage("cortex", "/predictive-coding/cortex", "Cortex & brain"),
        new DocsPage("llm", "/predictive-coding/llm", "Language"),
        new DocsPage("vision", "/predictive-coding/vision", "Vision & GPU"),
    };

    public static int IndexOf(string? key)
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            if (string.Equals(Pages[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
