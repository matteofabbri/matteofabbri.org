using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace TheQult.Pages;

// Uscita: solo POST (con token antiforgery) così un link o un form esterno non
// può disconnettere qualcuno. La GET mostra il pulsante.
public class LogoutModel : TheQultPageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData["Flash"] = "Logged out. The couches will miss you.";
        return RedirectToPage("/Index");
    }
}
