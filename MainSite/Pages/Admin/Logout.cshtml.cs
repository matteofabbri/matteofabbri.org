using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MainSite.Pages.Admin;

// Cancella solo il cookie del blog: la sessione della webmail su mailcow resta
// aperta (e viceversa), perché sono due cose diverse.
public sealed class LogoutModel : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/");
    }

    public IActionResult OnGet() => Redirect("/");
}
