using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheQult.Data;
using TheQult.Services;

namespace TheQult.Pages;

[Authorize]
public class RequestsModel : TheQultPageModel
{
    private readonly TheQultStore _store;
    private readonly CommunityService _community;

    public RequestsModel(TheQultStore store, CommunityService community)
    {
        _store = store;
        _community = community;
    }

    // Una riga dell'inbox: il soggiorno, il nome dell'altra parte e se la
    // referenza per quel soggiorno è ancora da scrivere.
    public sealed record StayRow(
        StayRequest Stay,
        string OtherName,
        bool OtherIsGuest,
        bool ReferencePending);

    public IReadOnlyList<StayRow> Incoming { get; private set; } = [];
    public IReadOnlyList<StayRow> Outgoing { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var stays = await _store.GetStaysInvolvingAsync(MemberId!, ct);

        var incoming = new List<StayRow>();
        var outgoing = new List<StayRow>();
        foreach (var stay in stays)
        {
            bool iAmHost = stay.HostId == MemberId;
            var other = await _store.GetMemberAsync(stay.OtherThan(MemberId!), ct);
            bool referencePending = stay.Status == StayRequest.StatusAccepted
                && await _store.GetReferenceAsync(MemberId!, stay.Id, ct) is null;

            var row = new StayRow(
                stay,
                other?.DisplayName ?? "A former member",
                !iAmHost,
                referencePending);

            if (iAmHost)
                incoming.Add(row);
            else
                outgoing.Add(row);
        }

        Incoming = incoming;
        Outgoing = outgoing;
    }

    public async Task<IActionResult> OnPostChangeAsync(string id, string action, CancellationToken ct)
    {
        var result = await _community.ChangeStayStatusAsync(MemberId!, id, action, ct);
        TempData["Flash"] = result.Ok
            ? action switch
            {
                "accept" => "Stay accepted. Sleep well, everyone.",
                "decline" => "Request declined.",
                _ => "Request cancelled.",
            }
            : Errors.For(result.Error);

        return RedirectToPage();
    }
}
