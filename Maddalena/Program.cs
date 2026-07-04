using Maddalena.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;

namespace Maddalena;

// Il sito MVC di maddalena.me. Caddy gli inoltra tutto il dominio, tranne /mail (webmail
// mailcow). Ospita anche il LICENSING sotto /licensing (compra + webhook Stripe + le mie
// licenze). Gli account sono LOCALI (ASP.NET Core Identity + cookie): il ruolo di
// provider OIDC e' stato rimosso quando il licensing e' entrato in Maddalena e nessun
// altro sito lo usava piu'.
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Telemetria, health check e service discovery condivisi della soluzione.
        builder.AddServiceDefaults();

        // Niente view lato server: il frontend sono i file STATICI in wwwroot
        // (HTML/CSS/JS puri, sostituibili senza toccare il C#); il backend sono
        // gli endpoint JSON /api/* dei controller.
        builder.Services.AddControllers();

        // Il contratto delle API, descritto in OpenAPI: servito su /openapi/v1.json.
        // E' IL documento da dare a chi fa il frontend (insieme a wwwroot).
        builder.Services.AddOpenApi();

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

        // PostgreSQL (Aspire): un solo contesto per gli account Identity + le licenze.
        builder.AddNpgsqlDbContext<ApplicationDbContext>("maddalenadb");

        // Identity SENZA la UI prefabbricata (niente pacchetto Identity.UI): le pagine
        // account (login, registrazione, gestione) sono nostre, in AccountController.
        builder.Services
            .AddIdentity<IdentityUser, IdentityRole>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;   // finche' non c'e' un invio email
                options.Password.RequiredLength = 8;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        // Chi arriva su un [Authorize] senza sessione: le /api/* ricevono un 401
        // secco (ci pensa il JavaScript della pagina), le navigazioni normali
        // (es. il link di download licenza) vanno alla pagina statica di login.
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else
                    context.Response.Redirect("/account.html?return="
                        + Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        // Stripe (licensing): la chiave segreta per le chiamate server (Checkout, webhook). Vuota finche'
        // non e' impostata, cosi' il sito parte comunque; la chiave di FIRMA (Licensing:PrivateKey) la legge
        // il webhook. Publishable/secret/webhook arrivano dall'AppHost come Stripe__*.
        Stripe.StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

        var app = builder.Build();

        // Pre-release: crea lo schema (Identity + licenze) all'avvio (niente migrazioni).
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();

        app.UseForwardedHeaders();
        app.UseDefaultFiles();         // "/" -> wwwroot/index.html
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();          // /api/* + download/webhook (attribute routing)
        app.MapOpenApi();              // /openapi/v1.json (il contratto delle API)

        // Redirect di cortesia: i vecchi URL delle pagine server portano alle statiche.
        // ExcludeFromDescription: non sono API, fuori dal contratto OpenAPI.
        app.MapGet("/licensing", () => Results.Redirect("/licensing.html")).ExcludeFromDescription();
        app.MapGet("/licensing/mine", () => Results.Redirect("/licenses.html")).ExcludeFromDescription();
        app.MapGet("/account", () => Results.Redirect("/account.html")).ExcludeFromDescription();
        app.MapGet("/account/login", () => Results.Redirect("/account.html")).ExcludeFromDescription();

        app.MapDefaultEndpoints();

        app.Run();
    }
}
