using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using TheQult.Data;
using TheQult.Services;

namespace TheQult.Pages;

// Il tetto per IP "auth" (20 richieste / 10 minuti) copre l'iscrizione.
[EnableRateLimiting("auth")]
public class JoinModel : TheQultPageModel
{
    private readonly CommunityService _community;

    public JoinModel(CommunityService community)
    {
        _community = community;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? Error { get; private set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Tell us your name.")]
        [MaxLength(60, ErrorMessage = "Names are up to 60 characters.")]
        [Display(Name = "Name (as you want it to appear)")]
        public string DisplayName { get; set; } = "";

        [Required(ErrorMessage = "We need an email for the login.")]
        [EmailAddress(ErrorMessage = "That email address doesn't look right.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Choose a password.")]
        [MinLength(8, ErrorMessage = "Passwords are at least 8 characters.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Display(Name = "City")]
        [MaxLength(80)]
        public string City { get; set; } = "";

        [Display(Name = "Country (two letters, like IT)")]
        [MaxLength(2)]
        public string Country { get; set; } = "";

        [Required(ErrorMessage = "Choose a hosting status — you can change it any time.")]
        [Display(Name = "Can you host?")]
        public string Hosting { get; set; } = Member.HostingNo;

        [Range(0, 16, ErrorMessage = "Guests per night: between 0 and 16.")]
        [Display(Name = "How many guests fit per night?")]
        public int MaxGuests { get; set; }

        [Display(Name = "Languages you speak")]
        [MaxLength(120)]
        public string Languages { get; set; } = "";

        [Display(Name = "About you")]
        [MaxLength(2000, ErrorMessage = "Keep the introduction under 2000 characters.")]
        public string About { get; set; } = "";
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return Page();

        var result = await _community.JoinAsync(new CommunityService.JoinInput(
            Input.Email, Input.Password, Input.DisplayName,
            Input.City, Input.Country, Input.Hosting,
            Input.MaxGuests, Input.Languages, Input.About), ct);

        if (!result.Ok)
        {
            Error = Errors.For(result.Error);
            return Page();
        }

        await SignInAsync(result.Value!);
        TempData["Flash"] = "Welcome to TheQult. Free, like it should be.";
        return RedirectToPage("/Profile");
    }

    // La sessione è un cookie con due claim: id del profilo (per le regole) e
    // nome pubblico (per l'intestazione). Nessuna password nel cookie.
    internal async Task SignInAsync(Member member)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, member.Id),
            new Claim(ClaimTypes.Name, member.DisplayName),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }
}
