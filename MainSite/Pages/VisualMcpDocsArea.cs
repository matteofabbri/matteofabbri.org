namespace MainSite.Pages;

// L'area documentaria di /visualmcp: le pagine in ordine di lettura, condivise
// dalla navigazione a schede e dal pie' di pagina precedente/successivo.
// Le chiavi corrispondono a ViewData["DocsKey"] su ogni pagina dell'area.
public static class VisualMcpDocsArea
{
    public sealed record DocsPage(string Key, string Href, string Label);

    public static readonly IReadOnlyList<DocsPage> Pages = new[]
    {
        new DocsPage("overview", "/visualmcp", "Overview"),
        new DocsPage("getting-started", "/visualmcp/getting-started", "Getting started"),
        new DocsPage("how-it-works", "/visualmcp/how-it-works", "How it works"),
        new DocsPage("tools", "/visualmcp/tools", "Capabilities"),
        new DocsPage("code", "/visualmcp/guides/code", "Read code"),
        new DocsPage("refactoring", "/visualmcp/guides/refactoring", "Refactor"),
        new DocsPage("razor", "/visualmcp/guides/razor", "Razor & binaries"),
        new DocsPage("runtime", "/visualmcp/guides/runtime", "Build & test"),
        new DocsPage("profiling", "/visualmcp/guides/profiling", "Profiling"),
        new DocsPage("workflow", "/visualmcp/guides/workflow", "Workflow"),
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
