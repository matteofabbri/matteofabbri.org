using System.Threading.RateLimiting;
using MainSite.Blog;
using MainSite.Booking;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;

namespace MainSite;

// Il sito personale (matteofabbri.org). Ospita anche i PROGETTI: oggi il proxy dei
// download di Sperone (DownloadController) e le prenotazioni dal calendario
// (BookingController), domani il resto di quello che era su maddalena.me.
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Telemetria, health check e service discovery condivisi della soluzione.
        builder.AddServiceDefaults();

        // Le API dei progetti sono controller con attribute routing (/api/*,
        // /download/*). Il blog, che ha bisogno di HTML per i motori di ricerca,
        // usa invece Razor Pages.
        builder.Services.AddControllers();

        // --- Log delle richieste su Sperone (embedded) --------------------------
        // La libreria Sperone gira nello stesso processo e scrive in
        // RequestLog:Path (in produzione il volume Docker /data/sperone).
        // Middleware + coda + writer in background; Enabled=false = nessun
        // middleware, nessun database, nessuna coda.
        builder.Services.Configure<RequestLogOptions>(builder.Configuration.GetSection(RequestLogOptions.Section));

        var requestLogOptions = builder.Configuration.GetSection(RequestLogOptions.Section).Get<RequestLogOptions>() ?? new();
        bool requestLogEnabled = requestLogOptions.Enabled;

        if (requestLogEnabled)
        {
            builder.Services.AddSingleton<RequestLogStore>();
            builder.Services.AddSingleton<RequestLogQueue>();
            builder.Services.AddSingleton<IRequestLogSink>(services => services.GetRequiredService<RequestLogQueue>());
            builder.Services.AddSingleton<RequestCountryResolver>();
            builder.Services.AddSingleton<ICountryResolver>(services => services.GetRequiredService<RequestCountryResolver>());
            builder.Services.AddHostedService<RequestLogWriter>();
        }

        // --- Blog: articoli su file, accesso delegato a mailcow ---------------
        // I contenuti stanno in una cartella Markdown (in produzione un volume
        // Docker, /data/blog), non in un database: sopravvivono ai deploy e si
        // possono modificare anche a mano. L'accesso usa il server OAuth2 di
        // mailcow: la password della casella non passa mai dal sito.
        builder.Services.Configure<BlogOptions>(builder.Configuration.GetSection(BlogOptions.Section));

        var blogOptions = builder.Configuration.GetSection(BlogOptions.Section).Get<BlogOptions>() ?? new();
        var blogAuthState = new BlogAuthState(
            blogOptions.Auth,
            builder.Environment.IsDevelopment());

        builder.Services.AddSingleton(blogAuthState);
        builder.Services.AddSingleton<BlogClock>();
        builder.Services.AddSingleton<BlogStore>();
        builder.Services.AddSingleton<BlogMediaStore>();
        builder.AddBlogAuthentication(blogAuthState);

        builder.Services.AddAuthorization(options =>
        {
            // Non basta un cookie qualsiasi: serve il claim che BlogAuth mette
            // solo dopo l'autenticazione su mailcow.
            options.AddPolicy(BlogAuth.Policy, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(BlogAuth.AuthorClaim, "true"));
        });

        // Le pagine admin sono tutte protette; login e logout devono restare
        // raggiungibili anche senza sessione (il logout per cancellarla).
        builder.Services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/Admin", BlogAuth.Policy);
            options.Conventions.AllowAnonymousToPage("/Admin/Login");
            options.Conventions.AllowAnonymousToPage("/Admin/Logout");
        });

        // --- Prenotazioni: il calendario CalDAV di SOGo (mailcow) -------------
        // Le regole (orari, slot, buffer, orizzonte) stanno in appsettings.json
        // sotto "Booking"; le credenziali arrivano dall'AppHost come
        // Calendar__CalDavUrl / Calendar__Username / Calendar__Password.
        builder.Services.Configure<CalendarOptions>(builder.Configuration.GetSection(CalendarOptions.Section));
        builder.Services.Configure<BookingOptions>(builder.Configuration.GetSection(BookingOptions.Section));

        if (builder.Configuration.GetValue<bool>($"{CalendarOptions.Section}:UseFake"))
        {
            // Solo sviluppo locale: calendario in memoria, nessuna credenziale.
            builder.Services.AddSingleton<ICalendarGateway, FakeCalendarGateway>();
        }
        else
        {
            builder.Services.AddHttpClient<ICalendarGateway, CalDavCalendarGateway>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
            });
        }

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<BookingService>();

        // Le API sono pubbliche e senza sessione: CORS aperto e nessuna
        // credenziale. Così la SPA (su un altro dominio) può chiamarle.
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        // Limite per IP sulle prenotazioni: 5 in 10 minuti. Dietro Caddy
        // l'indirizzo vero del client arriva in X-Forwarded-For (sotto).
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("booking", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "sconosciuto",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(10),
                    QueueLimit = 0,
                }));

            // Il login non verifica password (lo fa mailcow), ma la pagina e il
            // via vai OAuth non devono diventare un modo per martellare il server.
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "sconosciuto",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(10),
                    QueueLimit = 0,
                }));
        });

        // Dietro il NOSTRO Caddy: fidati degli header X-Forwarded-* (serve al
        // limite per IP, che altrimenti vedrebbe l'IP del proxy). Il container
        // non è pubblicato all'esterno: ci si arriva solo dal compose, quindi
        // non c'è nessuno a monte che possa falsificare l'header.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        // DownloadController (proxy dei download di Sperone). Due client GitHub distinti:
        //  - "github-releases": scarica gli asset binari via l'URL stabile /releases/latest/download/…
        //    (niente API, niente rate limit). Timeout largo: i pacchetti pesano ~60-100 MB.
        //  - "github-api": legge l'API release per versione e dimensioni (serve l'header Accept di GitHub).
        // Entrambi mandano uno User-Agent, che GitHub pretende; i redirect verso il CDN li seguono da soli.
        builder.Services.AddHttpClient("github-releases", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("matteofabbri.org-sperone-download");
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        builder.Services.AddHttpClient("github-api", client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("matteofabbri.org-sperone-download");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });

        // Cache in memoria: l'elenco dei download tiene l'ultima release di GitHub per 15 minuti,
        // cosi' non si chiama l'API a ogni visita (che ha un rate limit).
        builder.Services.AddMemoryCache();

        var app = builder.Build();

        app.UseForwardedHeaders();

        // Log richieste: dopo ForwardedHeaders (RemoteIpAddress è l'IP vero del
        // client) e prima del resto della pipeline (così vede anche static file,
        // 404 e richieste che l'autenticazione respinge).
        if (requestLogEnabled)
            app.UseMiddleware<RequestLogMiddleware>();

        // Serve i file statici da wwwroot (css, js, immagini, ...). La mappa
        // dei paesi è un .geojson: senza il mapping esplicito il middleware
        // non lo servirebbe (estensione sconosciuta).
        var staticFiles = new StaticFileOptions
        {
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
        };
        ((FileExtensionContentTypeProvider)staticFiles.ContentTypeProvider).Mappings[".geojson"] = "application/geo+json";
        app.UseStaticFiles(staticFiles);
        app.UseCors();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();          // /api/download/sperone, /api/booking/*, /blog/media/*
        app.MapRazorPages();           // /blog, /requests, /admin

        // Home → blog, feed RSS e sitemap (endpoint minimali, senza view).
        app.MapBlogEndpoints();

        app.MapDefaultEndpoints();

        app.Run();
    }
}
