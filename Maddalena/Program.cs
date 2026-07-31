using Maddalena.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;

namespace Maddalena;

// Il sito MVC di maddalena.me. Caddy gli inoltra tutto il dominio, tranne /mail (webmail
// mailcow). Ospita il LICENSING sotto /licensing (compra + webhook Stripe + le mie
// licenze). Gli account sono LOCALI (ASP.NET Core Identity + cookie): il ruolo di
// provider OIDC e' stato rimosso quando il licensing e' entrato in Maddalena e nessun
// altro sito lo usava piu'.
//
// IN RIFACIMENTO: i progetti stanno traslocando su MainSite (i download di Sperone sono
// gia' li'), mentre le API account (login/registrazione/password) e TUTTO l'LLM di
// predictive coding — controller, worker, crawler, librerie PredictiveCoding — sono
// stati rimossi. Resta il solo LicensingController; l'Identity sotto e' quello che il
// licensing usa per legare una licenza a un account.
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Telemetria, health check e service discovery condivisi della soluzione.
        builder.AddServiceDefaults();

        // Niente view lato server: il frontend e' la SPA React (repo maddelena-FE,
        // submodule external/maddelena-FE) che la build compila in wwwroot; il
        // backend sono gli endpoint JSON /api/* dei controller.
        builder.Services.AddControllers();

        // Il contratto delle API, descritto in OpenAPI: servito su /openapi/v1.json.
        // E' IL documento da dare a chi fa il frontend (la SPA ne tiene una copia
        // in src/openapi.json).
        builder.Services.AddOpenApi();

        // CORS aperto: le API si possono chiamare da qualunque origine. SENZA
        // credenziali pero' — '*' + cookie non convivono, e' una regola dei browser —
        // quindi il cookie di sessione non viaggia mai cross-origin e la protezione
        // CSRF resta intatta. Utile per: contratto OpenAPI da tool esterni, sviluppo
        // frontend in locale contro le chiamate anonime.
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

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

        // Identity SENZA la UI prefabbricata (niente pacchetto Identity.UI). ATTENZIONE:
        // con AccountController rimosso non c'e' piu' NESSUN endpoint di login o
        // registrazione — restano solo gli account gia' in tabella, che il licensing
        // usa come proprietari delle licenze. Da ridisegnare col resto di Maddalena.
        builder.Services
            .AddIdentity<IdentityUser, IdentityRole>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;   // finche' non c'e' un invio email
                options.Password.RequiredLength = 8;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        // Chi arriva su un [Authorize] senza sessione: le /api/* ricevono un 401
        // secco (ci pensa la SPA), le navigazioni normali (es. il link di download
        // licenza) vanno alla pagina licensing della SPA, che ha il login.
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else
                    context.Response.Redirect("/licensing");
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
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();          // /api/licensing/* + download licenza/webhook (attribute routing)
        app.MapOpenApi();              // /openapi/v1.json (il contratto delle API)

        // Redirect di cortesia: i vecchi URL (pagine server di ieri, statiche .html
        // dell'altro ieri) portano ai percorsi della SPA. Servono espliciti perche'
        // il fallback qui sotto ignora i percorsi con estensione (vincolo :nonfile).
        // ExcludeFromDescription: non sono API, fuori dal contratto OpenAPI.
        app.MapGet("/licensing.html", () => Results.Redirect("/licensing")).ExcludeFromDescription();
        app.MapGet("/licenses.html", () => Results.Redirect("/licensing")).ExcludeFromDescription();
        app.MapGet("/licensing/mine", () => Results.Redirect("/licensing")).ExcludeFromDescription();
        // Il session_id va preservato: un checkout Stripe aperto prima del deploy
        // torna ancora sul vecchio URL di successo.
        app.MapGet("/licensing-success.html", (HttpRequest req)
            => Results.Redirect("/licensing" + req.QueryString)).ExcludeFromDescription();
        app.MapGet("/licensing-cancel.html", () => Results.Redirect("/licensing")).ExcludeFromDescription();
        app.MapGet("/account.html", () => Results.Redirect("/")).ExcludeFromDescription();
        app.MapGet("/account", () => Results.Redirect("/")).ExcludeFromDescription();
        app.MapGet("/account/login", () => Results.Redirect("/")).ExcludeFromDescription();

        // La SPA fa routing lato client (/licensing, /sperone, ...): ogni percorso
        // senza estensione non gestito sopra serve index.html e ci pensa React.
        app.MapFallbackToFile("index.html").ExcludeFromDescription();

        app.MapDefaultEndpoints();

        app.Run();
    }
}
