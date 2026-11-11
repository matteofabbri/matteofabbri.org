using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

var builder = DistributedApplication.CreateBuilder(args);

// --- Publication environment: docker compose --------------------------------
// Defines the compose project that `aspire publish` generates.
builder.AddDockerComposeEnvironment("matteofabbri")
    .ConfigureComposeFile(file =>
    {
        // Volume degli articoli del blog: dichiararlo qui (e non solo sul
        // servizio) evita il compose "undefined volume". Docker lo crea al
        // primo avvio e lo conserva tra un deploy e l'altro: i post non
        // stanno nell'immagine, quindi non si perdono a ogni build.
        file.Volumes["blog-data"] = new Volume
        {
            Name = "blog-data",
            Driver = "local",
        };

        // Volume del database Sperone embedded (log delle richieste): come il
        // blog, i dati non stanno nell'immagine e sopravvivono ai deploy.
        file.Volumes["request-log-data"] = new Volume
        {
            Name = "request-log-data",
            Driver = "local",
        };

        // Volume del database Sperone della community TheQult (profili,
        // soggiorni, referenze): fuori dall'immagine, come tutti gli altri.
        file.Volumes["theqult-data"] = new Volume
        {
            Name = "theqult-data",
            Driver = "local",
        };

        // Local accounts: the SQLite file must survive deploys like the blog
        // and the request log.
        file.Volumes["auth-data"] = new Volume
        {
            Name = "auth-data",
            Driver = "local",
        };

        // Umami analytics database (PostgreSQL). Umami itself is stateless.
        file.Volumes["umami-db-data"] = new Volume
        {
            Name = "umami-db-data",
            Driver = "local",
        };

        // Bot detection: the model file is written by the retraining job and
        // read by the inference container (atomic os.replace, hot reload). The
        // SQLite dataset lives outside the image too.
        file.Volumes["botdetection-models"] = new Volume
        {
            Name = "botdetection-models",
            Driver = "local",
        };

        file.Volumes["botdetection-data"] = new Volume
        {
            Name = "botdetection-data",
            Driver = "local",
        };

        // RIAVVIO AUTOMATICO per tutti i servizi. Di suo Aspire non ne mette
        // nessuno (`restart-policy=no`), e le conseguenze sono due, entrambe
        // viste dal vivo: un container che esce per un errore passeggero — una
        // rete che cade a meta' di un download di ore — resta giu' per sempre,
        // e dopo un riavvio della macchina NON torna su niente, siti compresi.
        // "unless-stopped" riavvia sempre, tranne se l'ho fermato io a mano.
        foreach (var servizio in file.Services.Values)
        {
            servizio.Restart = "unless-stopped";
        }
    });

// --- Parametri configurabili (niente segreti hardcoded) ---------------------
// Mail dell'account Let's Encrypt usata da Caddy per i certificati.
var acmeEmail = builder.AddParameter("acme-email");

// Public site names. Caddy terminates TLS only for the SITES; every other
// name is not this machine's business (mail lives on OVH, not here).
// The sites are THREE (Umami is served under /analytics on the personal site):
//   - site-domain      -> personal site (e.g. matteofabbri.org)
//   - theqult-domain   -> theqult.org   (default here, override from .env)
//   - maddalena-domain -> maddalena.me  (default here, override from .env)
var siteDomain = builder.AddParameter("site-domain");
var thequltDomain = builder.AddParameter("theqult-domain", () => builder.Configuration["Parameters:theqult-domain"] ?? "theqult.org");
var maddalenaDomain = builder.AddParameter("maddalena-domain", () => builder.Configuration["Parameters:maddalena-domain"] ?? "maddalena.me");

// --- Il sito personale -------------------------------------------------------
// The booking API is UNCONFIGURED here: its backend was the mailcow stack
// (SOGo CalDAV) and it is not part of this stack any more. With no credentials
// the booking API answers 503. The blog author sign-in uses LOCAL accounts in
// the auth-data volume (Auth__DatabasePath); the first one is bootstrapped from
// Auth__BootstrapUsername/Password in the environment.

// --- Log delle richieste (Sperone embedded) ----------------------------------
// Il database è la libreria Sperone in-process, non un server: in produzione
// vive nel volume request-log-data montato in /data/sperone, in locale accanto
// al progetto come il blog. Nessun segreto da configurare.
string requestLogPath = builder.ExecutionContext.IsPublishMode ? "/data/sperone" : "App_Data/sperone";
string geoIpPath = builder.ExecutionContext.IsPublishMode
    ? "/data/geoip/dbip-city-lite.mmdb"
    : "App_Data/geoip/dbip-city-lite.mmdb";

// In pubblicazione gli articoli stanno nel volume; in esecuzione locale (run
// dell'AppHost) restano accanto al progetto, come con `dotnet run`.
string blogContentPath = builder.ExecutionContext.IsPublishMode ? "/data/blog" : "App_Data/blog";

// Local accounts: the SQLite database follows the same rule (volume in publish
// mode, project folder in local run).
string authPath = builder.ExecutionContext.IsPublishMode
    ? "/data/auth/auth.db"
    : "App_Data/auth/auth.db";

var site = builder.AddProject<Projects.MainSite>("site")
    .WithEnvironment("RequestLog__Path", requestLogPath)
    .WithEnvironment("RequestLog__GeoIpDatabasePath", geoIpPath)
    // Gli articoli stanno fuori dall'immagine: un deploy che ricostruisce il
    // container non deve portarsi via quello che è stato scritto.
    .WithEnvironment("Blog__ContentPath", blogContentPath)
    .WithEnvironment("Auth__DatabasePath", authPath)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.AddVolume(new Volume
        {
            Name = "blog-data",
            Type = "volume",
            Source = "blog-data",
            Target = "/data/blog",
        });

        // The local-account database survives every deploy for the same reason.
        service.AddVolume(new Volume
        {
            Name = "auth-data",
            Type = "volume",
            Source = "auth-data",
            Target = "/data/auth",
        });

        // Il database Sperone dei log vive fuori dall'immagine allo stesso modo.
        service.AddVolume(new Volume
        {
            Name = "request-log-data",
            Type = "volume",
            Source = "request-log-data",
            Target = "/data/sperone",
        });
    });

// --- theqult.org: la community di ospitalità ---------------------------------
// La versione rifatta meglio di Couchsurfing: profili, richieste di
// ospitalità e referenze, gratis per sempre. Il database è Sperone embedded
// come per i log: nel volume theqult-data (/data/theqult) in produzione, una
// cartella accanto al progetto in locale. Nessun segreto da configurare.
string thequltDataPath = builder.ExecutionContext.IsPublishMode ? "/data/theqult" : "App_Data/theqult";

var theqult = builder.AddProject<Projects.TheQult>("theqult")
    .WithEnvironment("TheQult__DataPath", thequltDataPath)
    .PublishAsDockerComposeService((_, service) =>
    {
        // I profili e le referenze NON stanno nell'immagine: un deploy non
        // deve portarseli via, mai.
        service.AddVolume(new Volume
        {
            Name = "theqult-data",
            Type = "volume",
            Source = "theqult-data",
            Target = "/data/theqult",
        });
    });

// --- maddalena.me: sito segnaposto -------------------------------------------
// Per ora risponde "hello world": il vecchio frontend React e il submodule
// maddelena-FE sono stati rimossi. Il servizio resta nello stack (dominio,
// Caddy e DNS già configurati) finché non arriverà il nuovo contenuto.
var maddalena = builder.AddProject<Projects.Maddalena>("maddalena");

// --- Umami analytics ---------------------------------------------------------
// Cookieless analytics under /analytics on the personal site. Umami needs
// PostgreSQL; both containers run only on the compose network and Caddy
// proxies /analytics to Umami. The image is built with BASE_PATH=/analytics
// (Umami's base path is a build-time setting), so this is a custom image, not
// the upstream one. Secrets come from the server .env; the website id is read
// by the site from the compose override after the website is created.
// Local `aspire run` needs usable defaults; in publish mode the empty value is
// the placeholder that the server .env fills before `docker compose up`.
var umamiDbPassword = builder.AddParameter("umami-db-password",
    () => builder.Configuration["Parameters:umami-db-password"]
          ?? (builder.ExecutionContext.IsPublishMode ? "" : "umami-dev-db-password"),
    secret: true);
var umamiAppSecret = builder.AddParameter("umami-app-secret",
    () => builder.Configuration["Parameters:umami-app-secret"]
          ?? (builder.ExecutionContext.IsPublishMode ? "" : "umami-dev-app-secret-change-me"),
    secret: true);
var umamiTwoFactorKey = builder.AddParameter("umami-two-factor-key",
    () => builder.Configuration["Parameters:umami-two-factor-key"]
          ?? (builder.ExecutionContext.IsPublishMode
              ? ""
              : "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
    secret: true);

var umamiDb = builder.AddContainer("umami-db", "postgres", "15-alpine")
    .WithEnvironment("POSTGRES_DB", "umami")
    .WithEnvironment("POSTGRES_USER", "umami")
    .WithEnvironment("POSTGRES_PASSWORD", umamiDbPassword)
    .WithVolume("umami-db-data", "/var/lib/postgresql/data")
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Healthcheck = new Healthcheck
        {
            Test = ["CMD-SHELL", "pg_isready -U umami -d umami"],
            Interval = "5s",
            Timeout = "5s",
            Retries = 5,
            StartPeriod = "10s",
        };
    });

var umami = builder.AddContainer("umami", "matteofabbri-umami", "latest")
    // The password is part of the connection string: Aspire writes the ${...}
    // placeholder and compose substitutes it from the server .env.
    .WithEnvironment("DATABASE_URL",
        ReferenceExpression.Create($"postgresql://umami:{umamiDbPassword}@umami-db:5432/umami"))
    .WithEnvironment("APP_SECRET", umamiAppSecret)
    .WithEnvironment("TWO_FACTOR_ENCRYPTION_KEY", umamiTwoFactorKey)
    .WithEnvironment("DISABLE_TELEMETRY", "1")
    .WithEnvironment("DISABLE_UPDATES", "1")
    // Caddy is the proxy in front of Umami: trust its X-Forwarded-For.
    .WithEnvironment("CLIENT_IP_HEADER", "X-Forwarded-For")
    .WithEnvironment("TRUSTED_PROXIES", "private")
    .WithHttpEndpoint(targetPort: 3000, name: "http")
    .WaitFor(umamiDb)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Init = true;
        service.DependsOn["umami-db"] = new ServiceDependency { Condition = "service_healthy" };
        service.Healthcheck = new Healthcheck
        {
            // BASE_PATH=/analytics moves the API under the prefix too.
            Test = ["CMD-SHELL", "curl -fsS http://localhost:3000/analytics/api/heartbeat || exit 1"],
            Interval = "5s",
            Timeout = "5s",
            Retries = 5,
            StartPeriod = "30s",
        };
    });

// L'indirizzo interno (host:porta) a cui Caddy inoltra il traffico del sito:
// deriva dall'endpoint del progetto. HostAndPort restituisce "host:porta" gia'
// risolti e si adatta da solo alla modalita': in pubblicazione diventa "site:8080",
// in esecuzione locale "aspire.dev.internal:<porta>". (TargetPort da solo NON si
// risolve in run: Caddy riceveva un template grezzo e non partiva.)
var siteUpstream = ReferenceExpression.Create(
    $"{site.GetEndpoint("http").Property(EndpointProperty.HostAndPort)}");
var thequltUpstream = ReferenceExpression.Create(
    $"{theqult.GetEndpoint("http").Property(EndpointProperty.HostAndPort)}");
var maddalenaUpstream = ReferenceExpression.Create(
    $"{maddalena.GetEndpoint("http").Property(EndpointProperty.HostAndPort)}");
var umamiUpstream = ReferenceExpression.Create(
    $"{umami.GetEndpoint("http").Property(EndpointProperty.HostAndPort)}");

// --- Caddy: the front door on ports 80/443 ----------------------------------
// Custom image (official Caddy + the layer4 module) built from the Dockerfile
// in ./caddy. The plugin reads the SNI and forwards the site TLS to the
// internal HTTP server with the PROXY protocol, so the real client IP survives
// (GeoIP, per-IP rate limits and /requests all depend on it).
var caddy = builder.AddDockerfile("caddy", "caddy")
    // /data conserva i certificati Let's Encrypt (solo dei nomi che Caddy
    // termina) tra un riavvio e l'altro.
    .WithVolume("caddy-data", "/data")
    .WithVolume("caddy-config", "/config")
    .WithEndpoint(port: 80, targetPort: 80, scheme: "http", name: "http")
    .WithEndpoint(port: 443, targetPort: 443, scheme: "https", name: "https")
    // Pubblica 80/443 sull'host: senza questo restano interni (solo expose).
    .WithExternalHttpEndpoints()
    .WithEnvironment("ACME_EMAIL", acmeEmail)
    .WithEnvironment("SITE_DOMAIN", siteDomain)
    .WithEnvironment("THEQULT_DOMAIN", thequltDomain)
    .WithEnvironment("MADDALENA_DOMAIN", maddalenaDomain)
    .WithEnvironment("SITE_UPSTREAM", siteUpstream)
    .WithEnvironment("THEQULT_UPSTREAM", thequltUpstream)
    .WithEnvironment("MADDALENA_UPSTREAM", maddalenaUpstream)
    .WithEnvironment("UMAMI_UPSTREAM", umamiUpstream)
    // Build from the Dockerfile in ./caddy instead of expecting a ready image:
    // the compose stays self-contained.
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Image = null;
        service.Build = new Build
        {
            Context = "./caddy",
            Dockerfile = "Dockerfile",
        };
    });

// --- Bot detection: the model labels the request log -------------------------
// No gateway and no blocking: the site's request-log writer sends batches to
// this container and stores isBot/isMaliciousURL on the entry. If the model is
// down the flags stay null and the log is written anyway.
// The build context is bot-detection/ because the image compiles
// contracts/bot_detection.proto with grpcio-tools and copies ai/.
var botAi = builder.AddDockerfile("bot-ai", "../bot-detection", "ai/Dockerfile")
    // h2c must reach the Python process directly: the DCP HTTP/1.1 proxy is
    // not in the gRPC path. In compose the endpoint resolves to bot-ai:50051.
    .WithHttpEndpoint(targetPort: 50051, name: "grpc", isProxied: false)
    .WithHttpEndpoint(targetPort: 8081, name: "http")
    .WithHttpHealthCheck("/healthz", endpointName: "http")
    .AsHttp2Service()
    .WithVolume("botdetection-models", "/models")
    .WithVolume("botdetection-data", "/data")
    .WithEnvironment("BOT_GRPC_PORT", "50051")
    .WithEnvironment("BOT_HTTP_PORT", "8081")
    .WithEnvironment("MODEL_PATH", "/models/bot_model.joblib")
    .WithEnvironment("DATASET_PATH", "/data/observations.sqlite3");

// The site starts even when the model is unavailable: the flags are optional.
site.WithEnvironment("BotDetection__GrpcUrl", botAi.GetEndpoint("grpc"));

builder.Build().Run();
