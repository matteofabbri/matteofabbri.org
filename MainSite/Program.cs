using System.Threading.RateLimiting;
using BotDetection.Client;
using MainSite.Analytics;
using MainSite.Auth;
using MainSite.Blog;
using MainSite.Booking;
using MainSite.InfoBots;
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
    public static async Task Main(string[] args)
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

        // Lo store Sperone e il resolver GeoIP servono anche a /info-send,
        // indipendentemente dal log delle richieste.
        builder.Services.AddSingleton<RequestLogStore>();
        builder.Services.AddSingleton<RequestCountryResolver>();
        builder.Services.AddSingleton<ICountryResolver>(services => services.GetRequiredService<RequestCountryResolver>());

        // Classificazione bot: il writer arricchisce i batch prima di scrivere
        // su Sperone. Se il modello non risponde i flag restano null e il log
        // viene scritto comunque.
        builder.Services.AddBotDetectionClient(builder.Configuration);
        builder.Services.AddSingleton<BotEnricher>();

        if (requestLogEnabled)
        {
            builder.Services.AddSingleton<RequestLogQueue>();
            builder.Services.AddSingleton<IRequestLogSink>(services => services.GetRequiredService<RequestLogQueue>());
            builder.Services.AddHostedService<RequestLogWriter>();
            builder.Services.AddHostedService<RequestLogBackfill>();
            builder.Services.AddHostedService<RequestLogReindex>();
        }

        // --- Blog: Markdown files, author sign-in with local accounts ---------
        // Content lives in a Markdown folder (a Docker volume in production,
        // /data/blog), not in a database: it survives deploys and can be edited
        // by hand. Authors are local accounts in SQLite (MainSite/Auth); no
        // external identity provider is involved.
        builder.Services.Configure<BlogOptions>(builder.Configuration.GetSection(BlogOptions.Section));
        builder.Services.Configure<LocalAuthOptions>(builder.Configuration.GetSection(LocalAuthOptions.Section));
        builder.Services.AddSingleton<LocalUserStore>();

        // --- Analytics: the Umami tracker is enabled by configuration ----------
        // Empty ScriptUrl/WebsiteId = no tracker. The layout skips /admin.
        builder.Services.Configure<UmamiOptions>(builder.Configuration.GetSection(UmamiOptions.Section));
        builder.Services.AddSingleton<BlogClock>();
        builder.Services.AddSingleton<BlogStore>();
        builder.Services.AddSingleton<SitePageStore>();
        builder.Services.AddSingleton<BlogMediaStore>();
        builder.Services.AddSingleton<JumbotronMediaStore>();
        builder.Services.AddSingleton<NotFoundMediaStore>();

        // Download dei video del blog: yt-dlp (wrapper cross-platform in Blog/YtDlp.cs)
        // salva nella cartella content/ del volume, da cui l'admin copia verso 404.
        builder.Services.Configure<YtDlpOptions>(builder.Configuration.GetSection(YtDlpOptions.Section));
        builder.Services.AddSingleton<BlogContentStore>();
        builder.Services.AddSingleton<YtDlp>();
        builder.Services.AddSingleton<PostMediaDownloader>();

        builder.AddBlogAuthentication();

        builder.Services.AddAuthorization(options =>
        {
            // A plain cookie is not enough: the claim is set by BlogAuth only
            // after a local account signed in.
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

        // --- Booking: CalDAV calendar -----------------------------------------
        // Rules (hours, duration, buffer, horizon) live in appsettings.json under
        // "Booking". The calendar account is fixed (matteo@matteofabbri.org);
        // Calendar__CalDavUrl and Calendar__Password configure the server. With
        // no URL or password the API answers 503 and the site starts anyway.
        builder.Services.Configure<CalendarOptions>(builder.Configuration.GetSection(CalendarOptions.Section));
        builder.Services.PostConfigure<CalendarOptions>(options =>
            options.Username = CalendarOptions.FixedUsername);
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

            // Sign-in checks a local password, but the page and the form must
            // not become a way to hammer the server.
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "sconosciuto",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(10),
                    QueueLimit = 0,
                }));

            // /info-send è pubblico: un tetto per IP evita che diventi un modo
            // per riempire il disco di payload.
            options.AddPolicy("info-send", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "sconosciuto",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
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

        // Il wrapper di yt-dlp scarica il binario ufficiale da GitHub la prima
        // volta (se non è già installato): UA obbligatorio e timeout largo.
        builder.Services.AddHttpClient(YtDlp.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("matteofabbri.org-blog-media");
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        // Cache in memoria: l'elenco dei download tiene l'ultima release di GitHub per 15 minuti,
        // cosi' non si chiama l'API a ogni visita (che ha un rate limit).
        builder.Services.AddMemoryCache();

        var app = builder.Build();

        // Create the users table and, on a brand-new install, the bootstrap
        // account. A broken auth database must not take the public site down.
        try
        {
            await app.Services.GetRequiredService<LocalUserStore>().InitializeAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Logger.LogError(
                ex,
                "Local accounts are unavailable: /admin cannot sign anyone in until the database is reachable.");
        }

        // Editable pages (/about): seed the Markdown on a brand-new volume. The
        // file then belongs to the admin editor and a deploy never overwrites it.
        try
        {
            await app.Services.GetRequiredService<SitePageStore>().EnsureSeededAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Logger.LogError(ex, "Editable pages are unavailable; /about will use the built-in copy.");
        }

        app.UseForwardedHeaders();

        // Log richieste: dopo ForwardedHeaders (RemoteIpAddress è l'IP vero del
        // client) e prima del resto della pipeline (così vede anche static file,
        // 404 e richieste che l'autenticazione respinge).
        if (requestLogEnabled)
            app.UseMiddleware<RequestLogMiddleware>();

        // 404: la pagina riusa il percorso /404 e pesca un media a caso da
        // wwwroot/404. Va prima dei file statici e del routing.
        app.UseStatusCodePagesWithReExecute("/404");

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
        app.MapInfoEndpoints();        // POST /info-send

        app.MapDefaultEndpoints();

        app.Run();
    }
}
