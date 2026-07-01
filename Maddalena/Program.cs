using Maddalena.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Maddalena;

// Il sito MVC di maddalena.me. Caddy gli inoltra tutto il dominio, tranne /mail (webmail
// mailcow). Ospita anche il LICENSING sotto /licensing (compra + webhook Stripe + le mie
// licenze). Maddalena fa anche da provider di identita' OIDC (server OpenIddict) per i
// sotto-siti: e' l'"ufficio passaporti".
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Telemetria, health check e service discovery condivisi della soluzione.
        builder.AddServiceDefaults();
        builder.Services.AddControllersWithViews();
        builder.Services.AddRazorPages();          // UI di Identity (login / registrazione / account)

        // Dietro Caddy: fidati degli header X-Forwarded-* cosi' Maddalena sa di
        // essere https://maddalena.me. L'emittente (issuer) dei token OIDC deve
        // essere quel nome pubblico, non l'host interno del container.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            // Siamo dietro il NOSTRO Caddy: niente lista di proxy fidati da mantenere.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        // PostgreSQL (Aspire): un solo contesto per gli account Identity + le
        // tabelle di OpenIddict. `UseOpenIddict()` aggiunge le tabelle al modello.
        builder.AddNpgsqlDbContext<ApplicationDbContext>("maddalenadb",
            configureDbContextOptions: options => options.UseOpenIddict());

        builder.Services
            .AddDefaultIdentity<IdentityUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;   // finche' non c'e' un invio email
                options.Password.RequiredLength = 8;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>();

        // Identity usa i nomi-claim standard di OpenIddict (sub, name, email, role).
        builder.Services.Configure<IdentityOptions>(options =>
        {
            options.ClaimsIdentity.UserNameClaimType = Claims.Name;
            options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
            options.ClaimsIdentity.EmailClaimType = Claims.Email;
            options.ClaimsIdentity.RoleClaimType = Claims.Role;
        });

        builder.Services.AddOpenIddict()
            // Persistenza delle entita' OpenIddict nello stesso DB EF di Identity.
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore().UseDbContext<ApplicationDbContext>();
            })
            // Il server: gli endpoint, il flusso, gli scope, i certificati.
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("connect/authorize")
                       .SetTokenEndpointUris("connect/token")
                       .SetUserInfoEndpointUris("connect/userinfo")
                       .SetEndSessionEndpointUris("connect/logout");

                // Emittente (iss) esplicito quando l'AppHost lo fornisce: in dev e'
                // l'URL localhost reale di Maddalena, in prod il dominio pubblico.
                // Cosi' iss e authority dei sotto-siti combaciano sempre (niente piu'
                // dipendenza dall'host della richiesta dietro proxy).
                var issuer = builder.Configuration["Oidc:Issuer"];
                if (!string.IsNullOrWhiteSpace(issuer))
                    options.SetIssuer(new Uri(issuer));

                // Authorization Code + PKCE: il flusso del BROWSER (app web server-side).
                options.AllowAuthorizationCodeFlow()
                       .RequireProofKeyForCodeExchange();

                options.RegisterScopes(Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.Roles);

                // Certificati di SVILUPPO (auto-firmati). TODO produzione: sostituire
                // con certificati veri persistiti (volume), altrimenti i token non
                // restano validi tra riavvii/istanze.
                options.AddDevelopmentEncryptionCertificate()
                       .AddDevelopmentSigningCertificate();

                // Access token come JWT firmato (non cifrato): cosi' i sotto-siti
                // (resource server, es. licensing) li validano da soli con la chiave
                // pubblica pubblicata da Maddalena (JWKS), senza dover decifrare.
                options.DisableAccessTokenEncryption();

                var aspnet = options.UseAspNetCore()
                       .EnableAuthorizationEndpointPassthrough()
                       .EnableTokenEndpointPassthrough()
                       .EnableUserInfoEndpointPassthrough()
                       .EnableEndSessionEndpointPassthrough();

                // In locale si gira su http: niente requisito HTTPS sul trasporto.
                if (builder.Environment.IsDevelopment())
                    aspnet.DisableTransportSecurityRequirement();
            })
            // La validazione: permette a Maddalena di validare i propri access token
            // (es. all'endpoint /connect/userinfo).
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        // Stripe (licensing): la chiave segreta per le chiamate server (Checkout, webhook). Vuota finche'
        // non e' impostata, cosi' il sito parte comunque; la chiave di FIRMA (Licensing:PrivateKey) la legge
        // il webhook. Publishable/secret/webhook arrivano dall'AppHost come Stripe__*.
        Stripe.StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

        var app = builder.Build();

        // Pre-release: crea lo schema (Identity + OpenIddict) all'avvio (niente migrazioni).
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();

        app.UseForwardedHeaders();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();          // gli endpoint /connect/* (attribute routing)
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        app.MapRazorPages();           // /Identity/Account/{Login,Register,Manage,...}
        app.MapDefaultEndpoints();

        app.Run();
    }
}
