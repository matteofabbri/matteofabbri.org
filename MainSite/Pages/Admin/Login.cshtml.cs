using MainSite.Auth;
using MainSite.Blog;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MainSite.Pages.Admin;

// Local-account sign-in: the password is verified against the SQLite user store
// and the cookie claim is the only proof of an authenticated author. No external
// identity provider is involved.
[EnableRateLimiting("login")]
public sealed class LoginModel(LocalUserStore users, ILogger<LoginModel> logger) : PageModel
{
    public string? Error { get; private set; }
    public string? ReturnUrl { get; private set; }
    public bool HasAccounts { get; private set; } = true;
    public bool DatabaseUnavailable { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null, CancellationToken ct = default)
    {
        if (BlogAuth.IsAuthor(User))
            return RedirectToPage("/Admin/Index");

        ReturnUrl = SafeReturnUrl(returnUrl);
        await LoadStateAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        string? username,
        string? password,
        string? returnUrl,
        CancellationToken ct)
    {
        ReturnUrl = SafeReturnUrl(returnUrl);
        string destination = ReturnUrl ?? "/admin";
        await LoadStateAsync(ct);

        if (DatabaseUnavailable)
        {
            Error = "The account database is unavailable. Check the site logs.";
            return Page();
        }

        string name = (username ?? "").Trim();
        if (name.Length == 0 || string.IsNullOrEmpty(password))
        {
            Error = "Username and password are required.";
            return Page();
        }

        LocalUserCredentials? account;
        try
        {
            account = await users.FindCredentialsAsync(name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the local account.");
            DatabaseUnavailable = true;
            Error = "The account database is unavailable. Check the site logs.";
            return Page();
        }

        // Always pay the PBKDF2 cost, so a missing username and a wrong password
        // are indistinguishable by timing.
        bool passwordOk = account is null
            ? LocalPasswordHasher.Verify(password, LocalPasswordHasher.DummyHash)
            : LocalPasswordHasher.Verify(password, account.PasswordHash);

        if (account is null || !account.IsEnabled || !passwordOk)
        {
            Error = "Invalid username or password.";
            return Page();
        }

        await users.TouchLoginAsync(account.Id, ct);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            BlogAuth.CreatePrincipal(account),
            new AuthenticationProperties { IsPersistent = false });

        return LocalRedirect(destination);
    }

    private async Task LoadStateAsync(CancellationToken ct)
    {
        try
        {
            HasAccounts = await users.AnyAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the local accounts.");
            DatabaseUnavailable = true;
        }
    }

    private string? SafeReturnUrl(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl)
        && !returnUrl!.StartsWith("/admin/login", StringComparison.OrdinalIgnoreCase)
            ? returnUrl
            : null;
}
