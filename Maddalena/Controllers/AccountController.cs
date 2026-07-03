using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Maddalena.Controllers;

// Le pagine account di Maddalena, fatte in casa al posto della UI prefabbricata di
// ASP.NET Core Identity: login, registrazione, logout e gestione minima (cambio
// password). Ci arriva chi clicca "log in" dal licensing e chi rimbalza da
// /connect/authorize senza sessione (il cookie punta qui, vedi Program.cs).
// Niente 2FA / conferma email / provider esterni: si aggiungono quando servono.
[Route("account")]
public sealed class AccountController(
    SignInManager<IdentityUser> signInManager,
    UserManager<IdentityUser> userManager) : Controller
{
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (signInManager.IsSignedIn(User))
            return SafeRedirect(returnUrl);
        return View(new LoginModel { ReturnUrl = returnUrl });
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        // Stessa ricerca del password grant: l'email e' anche lo username.
        var user = await userManager.FindByEmailAsync(model.Email)
            ?? await userManager.FindByNameAsync(model.Email);
        var result = user is null
            ? Microsoft.AspNetCore.Identity.SignInResult.Failed
            : await signInManager.PasswordSignInAsync(user, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
            return SafeRedirect(model.ReturnUrl);

        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? "Too many failed attempts. Try again in a few minutes."
            : "Invalid email or password.");
        return View(model);
    }

    [HttpGet("register")]
    public IActionResult Register(string? returnUrl = null)
    {
        if (signInManager.IsSignedIn(User))
            return SafeRedirect(returnUrl);
        return View(new RegisterModel { ReturnUrl = returnUrl });
    }

    [HttpPost("register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var user = new IdentityUser { UserName = model.Email, Email = model.Email };
        var result = await userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return SafeRedirect(model.ReturnUrl);
    }

    // Il logout del SITO (cookie): i client OIDC invece passano da /connect/logout.
    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return Redirect("/");
    }

    // La pagina account: email, cambio password, logout.
    [Authorize]
    [HttpGet("")]
    public IActionResult Index() => View(new ChangePasswordModel());

    [Authorize]
    [HttpPost("password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordModel model)
    {
        if (!ModelState.IsValid)
            return View("Index", model);

        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge();

        var result = await userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return View("Index", model);
        }

        // Il cambio password invalida il security stamp: si rinnova il cookie per restare loggati.
        await signInManager.RefreshSignInAsync(user);
        TempData["Message"] = "Password changed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("denied")]
    public IActionResult Denied() => View();

    // Solo URL locali: il returnUrl arriva dalla query, mai seguirlo verso host esterni.
    private IActionResult SafeRedirect(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : Redirect("/");
}

public sealed class LoginModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public sealed class RegisterModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required, DataType(DataType.Password), MinLength(8)]
    public string Password { get; set; } = "";

    [DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";

    public string? ReturnUrl { get; set; }
}

public sealed class ChangePasswordModel
{
    [Required, DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = "";

    [Required, DataType(DataType.Password), MinLength(8)]
    public string NewPassword { get; set; } = "";

    [DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";
}
