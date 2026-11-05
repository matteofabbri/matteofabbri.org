using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using TheQult.Data;
using TheQult.Services;

namespace TheQult.Pages;

[Authorize]
public class ReferenceNewModel : TheQultPageModel
{
    private readonly TheQultStore _store;
    private readonly CommunityService _community;

    public ReferenceNewModel(TheQultStore store, CommunityService community)
    {
        _store = store;
        _community = community;
    }

    public StayRequest? Stay { get; private set; }

    public Member? Subject { get; private set; }

    public string? Error { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Choose a verdict.")]
        [Display(Name = "Verdict")]
        public string Verdict { get; set; } = Reference.VerdictPositive;

        [Required(ErrorMessage = "Write a few words: this is what the community reads.")]
        [MinLength(1)]
        [MaxLength(1500, ErrorMessage = "Keep the reference under 1500 characters.")]
        [Display(Name = "The reference")]
        public string Text { get; set; } = "";
    }

    // Il soggiorno arriva da ?stay= (i link generano "stay") e resta nascosto
    // nel form. Il soggetto è SEMPRE l'altro della coppia: si scrive solo di
    // persone con cui si è davvero stati.
    [BindProperty(Name = "stay", SupportsGet = true)]
    public string? StayId { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadStayAsync(ct))
            return NotFound();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadStayAsync(ct))
            return NotFound();

        if (!ModelState.IsValid)
            return Page();

        var result = await _community.WriteReferenceAsync(
            MemberId!, StayId!, Input.Verdict, Input.Text, ct);
        if (!result.Ok)
        {
            Error = Errors.For(result.Error);
            return Page();
        }

        TempData["Flash"] = $"Reference published. It is the first thing {Subject?.DisplayName}'s next guest will read.";
        return RedirectToPage("/Member", new { id = Subject!.Id });
    }

    private async Task<bool> LoadStayAsync(CancellationToken ct)
    {
        Stay = await _store.GetStayAsync(StayId ?? "", ct);
        if (Stay is null || !Stay.Involves(MemberId!))
            return false;

        Subject = await _store.GetMemberAsync(Stay.OtherThan(MemberId!), ct);
        return true;
    }
}
