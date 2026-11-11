using System.Globalization;
using MainSite.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MainSite.Pages.Admin;

// Account management for the local identity provider. The page sits under
// /Admin, so only authenticated authors can reach it.
public sealed class UsersModel(
    LocalUserStore users,
    IOptions<LocalAuthOptions> options,
    ILogger<UsersModel> logger) : PageModel
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public IReadOnlyList<LocalUser> Accounts { get; private set; } = [];
    public bool LoadFailed { get; private set; }
    public bool BootstrapConfigured => !string.IsNullOrWhiteSpace(options.Value.BootstrapUsername);
    public int MinPasswordLength => users.MinPasswordLength;

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(
        string? username,
        string? displayName,
        string? email,
        string? password,
        CancellationToken ct)
    {
        try
        {
            var account = await users.CreateAsync(username, displayName, email, password ?? "", ct);
            TempData["Message"] = $"Account '{account.Username}' created.";
        }
        catch (Exception ex) when (ex is DuplicateUsernameException or LocalUserValidationException)
        {
            TempData["Error"] = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not create a local account.");
            TempData["Error"] = "Could not create the account. Check the site logs.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id, CancellationToken ct)
    {
        try
        {
            var account = await users.FindByIdAsync(id, ct);
            if (account is null)
            {
                TempData["Error"] = "Account not found.";
                return RedirectToPage();
            }

            await users.SetEnabledAsync(id, !account.IsEnabled, ct);
            TempData["Message"] = account.IsEnabled
                ? $"Account '{account.Username}' disabled."
                : $"Account '{account.Username}' enabled.";
        }
        catch (LastEnabledUserException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not change the account state.");
            TempData["Error"] = "Could not update the account. Check the site logs.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(int id, string? newPassword, CancellationToken ct)
    {
        try
        {
            bool updated = await users.SetPasswordAsync(id, newPassword ?? "", ct);
            if (updated)
                TempData["Message"] = "Password updated. Open sessions for that account were signed out.";
            else
                TempData["Error"] = "Account not found.";
        }
        catch (LocalUserValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not reset the account password.");
            TempData["Error"] = "Could not update the password. Check the site logs.";
        }

        return RedirectToPage();
    }

    public string FormatWhen(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"))
            .ToString("d MMM yyyy HH:mm", English);

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            Accounts = await users.ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the local accounts.");
            LoadFailed = true;
        }
    }
}
