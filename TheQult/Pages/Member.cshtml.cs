using Microsoft.AspNetCore.Mvc;
using TheQult.Data;

namespace TheQult.Pages;

public class MemberModel : TheQultPageModel
{
    private readonly TheQultStore _store;

    public MemberModel(TheQultStore store)
    {
        _store = store;
    }

    public Member? Profile { get; private set; }

    // Le referenze ricevute, con il nome dell'autore (l'altro della coppia).
    public IReadOnlyList<(Reference Item, string AuthorName)> References { get; private set; }
        = [];

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        Profile = await _store.GetMemberAsync(id, ct);
        if (Profile is null)
            return NotFound();

        var references = await _store.GetReferencesForAsync(Profile.Id, ct);
        var rows = new List<(Reference, string)>(references.Count);
        foreach (var reference in references)
        {
            var author = await _store.GetMemberAsync(reference.AuthorId, ct);
            rows.Add((reference, author?.DisplayName ?? "A former member"));
        }

        References = rows;
        return Page();
    }

    public bool IsSelf => MemberId is not null && MemberId == Profile?.Id;
}
