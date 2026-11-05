using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using TheQult.Data;
using TheQult.Services;

namespace TheQult.Pages;

// Il tetto per IP "auth" (20 richieste / 10 minuti) copre i tentativi di login.
[EnableRateLimiting("auth")]
public class LoginModel : TheQultPageModel
{
    private readonly TheQultStore _store;

    public LoginModel(TheQultStore store)
    {
        _store = store;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "We need your email.")]
        [EmailAddress(ErrorMessage = "That email address doesn't look right.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "We need your password.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return Page();

        var member = await _store.GetMemberByEmailAsync(Input.Email, ct);
        if (member is null || !PasswordHasher.Verify(Input.Password, member.PasswordHash))
        {
            // Messaggio unico: non dire a chi ascolta quale delle due cose era sbagliata.
            Error = "Email or password is wrong.";
            return Page();
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, member.Id),
            new Claim(ClaimTypes.Name, member.DisplayName),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
