using Microsoft.AspNetCore.Mvc;
using TheQult.Data;

namespace TheQult.Pages;

public class MembersModel : TheQultPageModel
{
    private readonly TheQultStore _store;

    public MembersModel(TheQultStore store)
    {
        _store = store;
    }

    // Filtri dell'elenco: arrivano dalla query string e restano nei link di
    // paginazione, come i chip della pagina /requests di MainSite.
    [BindProperty(SupportsGet = true)]
    public string? City { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Hosting { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageIndex { get; set; } = 1;

    public MembersPage Results { get; private set; } = MembersPage.Empty;

    public bool HasFilters => !string.IsNullOrWhiteSpace(City) || !string.IsNullOrWhiteSpace(Hosting);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Hosting = Hosting is Member.HostingHost or Member.HostingMaybe or Member.HostingNo
            ? Hosting
            : null;

        Results = await _store.ListMembersAsync(City, Hosting, PageIndex, pageSize: 24, ct);
    }

    public string Href(int page)
    {
        string city = Uri.EscapeDataString(City ?? "");
        string hosting = Uri.EscapeDataString(Hosting ?? "");
        return $"/members?city={city}&hosting={hosting}&pageIndex={page}";
    }
}
