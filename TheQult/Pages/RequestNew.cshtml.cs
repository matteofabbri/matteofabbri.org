using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using TheQult.Data;
using TheQult.Services;

namespace TheQult.Pages;

[Authorize]
public class RequestNewModel : TheQultPageModel
{
    private readonly TheQultStore _store;
    private readonly CommunityService _community;

    public RequestNewModel(TheQultStore store, CommunityService community)
    {
        _store = store;
        _community = community;
    }

    public Member? Host { get; private set; }

    public string? Error { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Pick an arrival date.")]
        [Display(Name = "Arrival")]
        public DateOnly Arrival { get; set; }

        [Required(ErrorMessage = "Pick a departure date.")]
        [Display(Name = "Departure")]
        public DateOnly Departure { get; set; }

        [Display(Name = "A message for your host")]
        [MaxLength(1000, ErrorMessage = "Keep the message under 1000 characters.")]
        public string Message { get; set; } = "";
    }

    // L'host arriva da ?host= e resta nascosto nel form: è l'unica cosa che il
    // POST deve ridichiarare, il resto sono date e parole dell'ospite. Il nome
    // del parametro query è "host" (è così che i link generano), non "HostId".
    [BindProperty(Name = "host", SupportsGet = true)]
    public string? HostId { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Host = await _store.GetMemberAsync(HostId ?? "", ct);
        if (Host is null)
            return NotFound();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Host = await _store.GetMemberAsync(HostId ?? "", ct);
        if (Host is null)
            return NotFound();

        if (!ModelState.IsValid)
            return Page();

        var result = await _community.RequestStayAsync(
            MemberId!, Host.Id, Input.Arrival, Input.Departure, Input.Message, ct);
        if (!result.Ok)
        {
            Error = Errors.For(result.Error);
            return Page();
        }

        TempData["Flash"] = $"Request sent to {Host.DisplayName}. Now it is in their hands.";
        return RedirectToPage("/Requests");
    }
}
