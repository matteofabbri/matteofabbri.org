using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheQult.Data;
using TheQult.Services;

namespace TheQult.Pages;

[Authorize]
public class ProfileModel : TheQultPageModel
{
    private readonly TheQultStore _store;
    private readonly CommunityService _community;

    public ProfileModel(TheQultStore store, CommunityService community)
    {
        _store = store;
        _community = community;
    }

    [BindProperty]
    public CommunityService.ProfileInput Input { get; set; } = new("", "", "", Member.HostingNo, 0, "", "");

    public string? Error { get; private set; }

    public Member? Profile { get; private set; }

    public IReadOnlyList<Reference> References { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Profile = await _store.GetMemberAsync(MemberId!, ct);
        if (Profile is null)
            return NotFound();

        Input = new CommunityService.ProfileInput(
            Profile.DisplayName, Profile.City, Profile.Country,
            Profile.Hosting, Profile.MaxGuests, Profile.Languages, Profile.About);

        References = await _store.GetReferencesForAsync(Profile.Id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            Profile = await _store.GetMemberAsync(MemberId!, ct);
            References = await _store.GetReferencesForAsync(MemberId!, ct);
            return Page();
        }

        var result = await _community.UpdateProfileAsync(MemberId!, Input, ct);
        if (!result.Ok)
        {
            Error = Errors.For(result.Error);
            Profile = await _store.GetMemberAsync(MemberId!, ct);
            References = await _store.GetReferencesForAsync(MemberId!, ct);
            return Page();
        }

        TempData["Flash"] = "Profile saved.";
        return RedirectToPage();
    }
}
