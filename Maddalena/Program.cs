using Maddalena.Data;
using Maddalena.Llm;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using PredictiveCoding.Device;

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
        // Sceglie il backend di calcolo dell'LLM una volta all'avvio: se la macchina
        // ha una GPU NVIDIA col CUDA Toolkit, le matmul vanno su cuBLAS; altrimenti
        // resta la CPU (SIMD+multithread). Il rilevamento fallisce in silenzio senza
        // le librerie CUDA, quindi sul server (AMD, niente CUDA) e' un no-op -> CPU.
        DeviceRegistry.AutoSelect();

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

        // DownloadController (proxy dei download di Sperone). Due client GitHub distinti:
        //  - "github-releases": scarica gli asset binari via l'URL stabile /releases/latest/download/…
        //    (niente API, niente rate limit). Timeout largo: i pacchetti pesano ~60-100 MB.
        //  - "github-api": legge l'API release per versione e dimensioni (serve l'header Accept di GitHub).
        // Entrambi mandano uno User-Agent, che GitHub pretende; i redirect verso il CDN li seguono da soli.
        builder.Services.AddHttpClient("github-releases", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("maddalena.me-sperone-download");
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        builder.Services.AddHttpClient("github-api", client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("maddalena.me-sperone-download");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });

        // Cache in memoria: l'elenco dei download tiene l'ultima release di GitHub per 15 minuti,
        // cosi' non si chiama l'API a ogni visita (che ha un rate limit).
        builder.Services.AddMemoryCache();

        // L'LLM di predictive coding (librerie PredictiveCoding.LLM/.Data del repo
        // PredicitiveCoding accanto a questo): LlmWorker lo riprende dalla cartella
        // di stato (in prod il volume docker maddalena-llm) o lo crea, lo traina
        // dalle datasources e lo consegna al singleton LlmService; LlmController
        // lo espone su /api/llm come interroga/istruisci/stato. Configurazione:
        // sezione "Llm" (env Llm__*).
        // In piu' il worker tiene vivo un crawler Abot (coda su Postgres, tabella
        // llm_crawl_queue) che nutre l'LLM di pagine web, e un timer che ne salva
        // lo stato ogni Llm__SaveEveryMinutes minuti.
        builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Llm"));
        builder.Services.AddSingleton<LlmCrawlStore>();
        builder.Services.AddSingleton<LlmService>();
        builder.Services.AddHostedService<LlmWorker>();

        // Log dell'LLM su Postgres (tabella llm_log) + un provider di logging che
        // cattura tutte le categorie "Maddalena.Llm.*" e le persiste. Visibili su
        // GET /api/llm/logs. Il flush gira come hosted service (stessa istanza).
        builder.Services.AddSingleton<LlmLogStore>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LlmLogStore>());
        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new PostgresLlmLoggerProvider(sp.GetRequiredService<LlmLogStore>()));

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

        app.MapControllers();          // /api/* + download/webhook (attribute routing)
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
