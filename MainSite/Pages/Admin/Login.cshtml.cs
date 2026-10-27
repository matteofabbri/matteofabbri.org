using System.Security.Claims;
using MainSite.Blog;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MainSite.Pages.Admin;

// L'accesso al blog non chiede mai una password: porta su mailcow, che
// autentica la casella (2FA compreso) e restituisce un profilo. Qui resta solo
// il cookie del blog, con il claim che la policy BlogAuthor richiede.
[EnableRateLimiting("login")]
public sealed class LoginModel(BlogAuthState auth) : PageModel
{
    public bool Enabled => auth.Enabled;
    public bool Fake => auth.UseFake;
    public string FakeAuthor => auth.FakeAuthor;
    public string? Error { get; private set; }
    public string? ReturnUrl { get; private set; }

    public string MailcowHost =>
        Uri.TryCreate(auth.Options.BaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : auth.Options.BaseUrl;

    public IActionResult OnGet(string? returnUrl = null, string? error = null)
    {
        if (auth.IsAuthor(User))
            return RedirectToPage("/Admin/Index");

        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        Error = error switch
        {
            "access_denied" => "Accesso non riuscito. Riprova; se il problema resta, controlla che la casella sia abilitata.",
            "not_configured" => "L'accesso con mailcow non è configurato su questo sito.",
            _ => null,
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null, CancellationToken ct = default)
    {
        string destination = Url.IsLocalUrl(returnUrl) && !returnUrl!.StartsWith("/admin/login", StringComparison.OrdinalIgnoreCase)
            ? returnUrl
            : "/admin";

        if (!auth.Enabled)
            return RedirectToPage(new { error = "not_configured" });

        // Solo sviluppo: nessun redirect e nessun cookie di mailcow.
        if (auth.UseFake)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, auth.FakeAuthor),
                new Claim(ClaimTypes.Email, auth.FakeAuthor),
                new Claim(ClaimTypes.Name, auth.FakeAuthor),
                new Claim(BlogAuth.AuthorClaim, "true"),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = false });

            return LocalRedirect(destination);
        }

        // Da qui in poi parla il middleware OAuth: authorize → mailcow →
        // callback → token → profilo → cookie.
        return new ChallengeResult(
            BlogAuth.Scheme,
            new AuthenticationProperties { RedirectUri = destination });
    }
}
