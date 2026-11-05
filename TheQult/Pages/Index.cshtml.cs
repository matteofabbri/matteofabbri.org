using TheQult.Data;

namespace TheQult.Pages;

public class IndexModel : TheQultPageModel
{
    private readonly TheQultStore _store;

    public IndexModel(TheQultStore store)
    {
        _store = store;
    }

    // I numeri della community, onesti anche quando sono zero.
    public int Members { get; private set; }
    public int Hosts { get; private set; }
    public int References { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Members = await _store.CountMembersAsync(ct);
        Hosts = await _store.CountHostsAsync(ct);
        References = await _store.CountReferencesAsync(ct);
    }
}
