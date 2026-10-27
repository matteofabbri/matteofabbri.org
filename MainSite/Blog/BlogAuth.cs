using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace MainSite.Blog;

// Accesso all'area di scrittura delegato a mailcow.
//
// mailcow espone un server OAuth2 (authorization code): il blog manda l'utente
// su https://mail.maddalena.me/oauth/authorize, mailcow lo fa autenticare con
// la sua webmail (2FA compreso), e alla fine torna qui con un codice. Il codice
// diventa un token, il token diventa un profilo, il profilo diventa il nostro
// cookie. La password della casella non passa mai dal sito.
//
// Qualunque casella autenticata da mailcow può scrivere: non c'è un elenco di
// autori da configurare. Gli account amministratore di mailcow non sono caselle
// e quindi non possono entrare.
public static class BlogAuth
{
    public const string Scheme = "mailcow";
    public const string Policy = "BlogAuthor";
    public const string AuthorClaim = "blog_author";

    public const string CookieName = "mf-blog-auth";

    // Cookie valido con il claim che BlogAuth mette solo dopo che mailcow ha
    // autenticato la casella.
    public static bool IsAuthor(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.HasClaim(AuthorClaim, "true");

    public static void AddBlogAuthentication(this WebApplicationBuilder builder, BlogAuthState state)
    {
        var authentication = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        });

        authentication.AddCookie(options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // In sviluppo si gira in HTTP; in produzione il sito è sempre dietro
            // Caddy con TLS, quindi il cookie non deve mai uscire in chiaro.
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.LoginPath = "/admin/login";
            options.LogoutPath = "/admin/logout";
            options.AccessDeniedPath = "/admin/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
        });

        // Senza client OAuth configurato il blog resta leggibile: semplicemente
        // non c'è modo di entrare in /admin (la pagina di login lo spiega).
        if (!state.Configured)
            return;

        authentication.AddOAuth(Scheme, options =>
        {
            options.ClientId = state.Options.ClientId.Trim();
            options.ClientSecret = state.Options.ClientSecret.Trim();
            options.AuthorizationEndpoint = state.Options.AuthorizeUrl;
            options.TokenEndpoint = state.Options.TokenUrl;
            options.UserInformationEndpoint = state.Options.ProfileUrl;
            options.CallbackPath = "/admin/login/callback";
            options.Scope.Clear();
            options.Scope.Add("profile");
            options.SaveTokens = false;
            // Se mailcow non risponde, meglio un errore in fretta che una
            // pagina di login appesa per un minuto e mezzo.
            options.BackchannelTimeout = TimeSpan.FromSeconds(15);

            options.Events.OnCreatingTicket = async context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("MainSite.Blog.BlogAuth");

                JsonElement profile;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, options.UserInformationEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);

                    using HttpResponseMessage response = await context.Backchannel.SendAsync(
                        request, context.HttpContext.RequestAborted);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogWarning("mailcow /oauth/profile ha risposto {Status}.", (int)response.StatusCode);
                        context.Fail($"mailcow /oauth/profile ha risposto {(int)response.StatusCode}.");
                        return;
                    }

                    using var payload = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                    // Clone: il documento viene rilasciato alla fine del try.
                    profile = payload.RootElement.Clone();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    logger.LogWarning(ex, "Impossibile leggere il profilo mailcow.");
                    context.Fail("mailcow non ha risposto.");
                    return;
                }

                if (!profile.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                {
                    context.Fail("mailcow non ha restituito un profilo valido.");
                    return;
                }

                string email = ReadString(profile, "email");
                if (email.Length == 0)
                    email = ReadString(profile, "username");

                if (email.Length == 0)
                {
                    context.Fail("Il profilo mailcow non contiene un indirizzo.");
                    return;
                }

                string name = ReadString(profile, "displayName");
                if (name.Length == 0 || name == "mailcow administrative user")
                    name = ReadString(profile, "full_name");
                if (name.Length == 0)
                    name = email;

                var identity = context.Identity;
                if (identity is null)
                {
                    context.Fail("Identità mailcow mancante.");
                    return;
                }

                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, email));
                identity.AddClaim(new Claim(ClaimTypes.Email, email));
                identity.AddClaim(new Claim(ClaimTypes.Name, name));
                // Il claim che apre l'area di scrittura: lo riceve qualunque
                // casella che mailcow ha appena autenticato.
                identity.AddClaim(new Claim(AuthorClaim, "true"));
            };

            // Qualunque errore (codice scaduto, casella revocata, token
            // rifiutato) finisce sulla pagina di login con un messaggio umano.
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();

                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("MainSite.Blog.BlogAuth");
                logger.LogWarning(context.Failure, "Accesso al blog non riuscito.");

                context.Response.Redirect("/admin/login?errore=accesso");
                return Task.CompletedTask;
            };
        });
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";
}
