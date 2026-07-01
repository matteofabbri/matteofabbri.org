using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Maddalena.Controllers;

// Gli endpoint OIDC che Maddalena espone come "ufficio passaporti":
//   /connect/authorize -> mostra il login (se serve) e rilascia il codice
//   /connect/token     -> scambia il codice con i token
//   /connect/userinfo  -> dati dell'utente per il client
//   /connect/logout    -> chiude la sessione
// Schema standard OpenIddict per il flusso Authorization Code + PKCE.
public class AuthorizationController(
    SignInManager<IdentityUser> signInManager,
    UserManager<IdentityUser> userManager,
    IOpenIddictScopeManager scopeManager) : Controller
{
    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("La richiesta OpenID Connect non e' disponibile.");

        // Se l'utente non e' loggato, rimbalzalo al login di Identity e poi torna qui.
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (result is not { Succeeded: true })
        {
            return Challenge(
                authenticationSchemes: IdentityConstants.ApplicationScheme,
                properties: new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path + QueryString.Create(
                        Request.HasFormContentType ? Request.Form.ToList() : Request.Query.ToList()),
                });
        }

        var user = await userManager.GetUserAsync(result.Principal)
            ?? throw new InvalidOperationException("Impossibile recuperare l'utente loggato.");

        var identity = await BuildIdentityAsync(user, request.GetScopes());
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("La richiesta OpenID Connect non e' disponibile.");

        // BROWSER: il codice di autorizzazione contiene gia' il principal, lo ri-firmiamo.
        if (request.IsAuthorizationCodeGrantType())
        {
            var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (result.Principal is null)
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            return SignIn(result.Principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        // TOOL CLI (sperone-setup): email+password -> verifica contro Identity -> token.
        if (request.IsPasswordGrantType())
        {
            var user = await userManager.FindByEmailAsync(request.Username!)
                ?? await userManager.FindByNameAsync(request.Username!);
            if (user is null)
                return ForbidInvalidGrant("Email o password non validi.");

            var check = await signInManager.CheckPasswordSignInAsync(user, request.Password!, lockoutOnFailure: true);
            if (!check.Succeeded)
                return ForbidInvalidGrant("Email o password non validi.");

            var identity = await BuildIdentityAsync(user, request.GetScopes());
            return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new InvalidOperationException("Il grant type richiesto non e' supportato.");
    }

    // Costruisce l'identita' che finira' nei token: sub, email, name, scope e
    // destinazioni (in quale token va ogni claim). Condivisa da authorize e password.
    private async Task<ClaimsIdentity> BuildIdentityAsync(IdentityUser user, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(
            authenticationType: "OpenIddict",
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, await userManager.GetUserIdAsync(user))
                .SetClaim(Claims.Email, await userManager.GetEmailAsync(user))
                .SetClaim(Claims.Name, await userManager.GetUserNameAsync(user));

        identity.SetScopes(scopes);

        var resources = new List<string>();
        await foreach (var resource in scopeManager.ListResourcesAsync(identity.GetScopes()))
            resources.Add(resource);
        identity.SetResources(resources);

        identity.SetDestinations(GetDestinations);
        return identity;
    }

    private ForbidResult ForbidInvalidGrant(string description) => Forbid(
        authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }));

    [Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [Produces("application/json")]
    public async Task<IActionResult> Userinfo()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Challenge(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = await userManager.GetUserIdAsync(user),
        };

        if (User.HasScope(Scopes.Email))
            claims[Claims.Email] = (await userManager.GetEmailAsync(user))!;

        if (User.HasScope(Scopes.Profile))
            claims[Claims.Name] = (await userManager.GetUserNameAsync(user))!;

        return Ok(claims);
    }

    [HttpGet("~/connect/logout")]
    [HttpPost("~/connect/logout")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> LogoutPost()
    {
        await signInManager.SignOutAsync();

        return SignOut(
            authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            properties: new AuthenticationProperties { RedirectUri = "/" });
    }

    // Decide in quale token (access e/o identity) finisce ogni claim.
    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Name:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Profile))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Email))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Role:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Roles))
                    yield return Destinations.IdentityToken;
                yield break;

            // Il security stamp di Identity non deve mai uscire nei token.
            case "AspNet.Identity.SecurityStamp":
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
