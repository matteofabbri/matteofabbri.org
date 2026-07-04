using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Maddalena.Controllers;

// Le "funzioni account" di Maddalena come API JSON: login, registrazione, logout,
// cambio password e "chi sono". La UI sta nei file statici (wwwroot/account.html):
// il server non produce HTML. La sessione resta il cookie di Identity; contro il
// CSRF bastano il cookie SameSite e il fatto che qui si accetta solo JSON (un form
// cross-site non puo' produrre nessuna delle due cose).
[ApiController]
[Route("api/account")]
public sealed class AccountController(
    SignInManager<IdentityUser> signInManager,
    UserManager<IdentityUser> userManager) : ControllerBase
{
    // Chi e' l'utente della sessione corrente: { email } oppure 401.
    // E' la chiamata con cui ogni pagina statica decide cosa mostrare.
    [HttpGet("me")]
    public IActionResult Me() =>
        signInManager.IsSignedIn(User)
            ? Ok(new { email = User.Identity!.Name })
            : Unauthorized();

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        // Stessa ricerca di sempre: l'email e' anche lo username.
        var user = await userManager.FindByEmailAsync(request.Email)
            ?? await userManager.FindByNameAsync(request.Email);
        var result = user is null
            ? Microsoft.AspNetCore.Identity.SignInResult.Failed
            : await signInManager.PasswordSignInAsync(user, request.Password, request.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
            return NoContent();

        return Unauthorized(new
        {
            message = result.IsLockedOut
                ? "Too many failed attempts. Try again in a few minutes."
                : "Invalid email or password.",
        });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var user = new IdentityUser { UserName = request.Email, Email = request.Email };
        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return IdentityProblem(result);

        await signInManager.SignInAsync(user, isPersistent: false);
        return NoContent();
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return NoContent();
    }

    [Authorize]
    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Unauthorized();

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return IdentityProblem(result);

        // Il cambio password invalida il security stamp: si rinnova il cookie per restare loggati.
        await signInManager.RefreshSignInAsync(user);
        return NoContent();
    }

    // Errori di Identity nella stessa forma dei 400 di validazione automatici
    // ([ApiController]): { errors: { "": [messaggi] } }. Il JS li appiattisce.
    private IActionResult IdentityProblem(IdentityResult result)
    {
        foreach (var error in result.Errors)
            ModelState.AddModelError(string.Empty, error.Description);
        return ValidationProblem(ModelState);
    }
}

public sealed class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";

    public bool RememberMe { get; set; }
}

public sealed class RegisterRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required, MinLength(8)]
    public string Password { get; set; } = "";
}

public sealed class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = "";

    [Required, MinLength(8)]
    public string NewPassword { get; set; } = "";
}
