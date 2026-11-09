using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

var builder = DistributedApplication.CreateBuilder(args);

// --- Ambiente di pubblicazione: docker compose ------------------------------
// Definisce il progetto compose generato da `aspire publish`.
// Qui dichiariamo anche la rete condivisa "neutra" (il "corridoio"):
// External = true => NON la possiede questo stack. La crei a mano una volta
//   docker network create matteo-shared
// e la dichiari uguale anche nel compose di mailcow. Cosi' gli aggiornamenti
// di mailcow non disturbano il collegamento.
// La chiave del dizionario diventa il nome della rete nel compose, quindi e'
// anche il nome da usare in `docker network create matteo-shared`.
const string SharedNetworkKey = "matteo-shared";

builder.AddDockerComposeEnvironment("matteofabbri")
    .ConfigureComposeFile(file =>
    {
        file.Networks[SharedNetworkKey] = new Network
        {
            Name = SharedNetworkKey,
            External = true,
        };

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

// I nomi pubblici. Caddy termina solo i SITI; ogni altro nome (posta, nome
// OVH di transizione, ...) passa sigillato a mailcow senza toccare nulla qui.
// Oggi i siti sono TRE:
//   - site-domain      -> sito personale (es. matteofabbri.org)
//   - theqult-domain   -> theqult.org   (default impostato qui, override da .env)
//   - maddalena-domain -> maddalena.me  (default impostato qui, override da .env)
//   - webmail-host     -> dove vive la webmail, bersaglio del redirect /mail
//                         (es. mail.matteofabbri.org; in transizione il nome OVH)
var siteDomain = builder.AddParameter("site-domain");
var thequltDomain = builder.AddParameter("theqult-domain", () => builder.Configuration["Parameters:theqult-domain"] ?? "theqult.org");
var maddalenaDomain = builder.AddParameter("maddalena-domain", () => builder.Configuration["Parameters:maddalena-domain"] ?? "maddalena.me");
var webmailHost = builder.AddParameter("webmail-host");

// Dove Caddy raggiunge mailcow, per nome sulla rete condivisa.
//   - mailcow-tls-upstream  -> nginx di mailcow, porta HTTPS (es. nginx-mailcow:8443)
//   - mailcow-http-upstream -> nginx di mailcow, porta HTTP  (es. nginx-mailcow:8080)
var mailcowTlsUpstream = builder.AddParameter("mailcow-tls-upstream");
var mailcowHttpUpstream = builder.AddParameter("mailcow-http-upstream");

// --- Il sito personale -------------------------------------------------------
// Ospita anche le prenotazioni degli appuntamenti, che leggono e scrivono il
// calendario della casella: è il calendario CalDAV di SOGo, il groupware dentro
// mailcow. L'URL è pubblico (SOGo lo serve sotto /SOGo/dav/ sullo stesso nome
// della webmail) così vale sia dal container sia dal PC in sviluppo.
var caldavUrl = builder.AddParameter("caldav-url",
    () => builder.Configuration["Parameters:caldav-url"] ?? "https://mail.maddalena.me/SOGo/dav/");

// La casella è fissa nel codice (matteo@matteofabbri.org); qui serve solo la
// password, che NON è quella vera della casella ma un'APP PASSWORD creata in
// mailcow con accesso DAV (le password vere cambiano, e con 2FA i client DAV
// non funzionano affatto). Vuota = prenotazioni spente (503), il sito parte.
//   dotnet user-secrets set "Parameters:caldav-password" "<app password DAV>" --project <questo AppHost>
var caldavPassword = builder.AddParameter("caldav-password",
    () => builder.Configuration["Parameters:caldav-password"] ?? "", secret: true);

// --- Blog: accesso con mailcow (OAuth2) --------------------------------------
// mailcow espone un server OAuth2: il blog manda l'autore sulla webmail, riceve
// un codice e lo scambia con il profilo della casella. La password non passa
// mai dal sito e il 2FA di mailcow continua a funzionare.
//
// Il client si crea in mailcow (Configurazione → Access → OAuth2 Apps) e il suo
// redirect URI deve essere esattamente:
//   https://<SITE_DOMAIN>/admin/login/callback
// Client ID e secret NON stanno nel repo: arrivano dal .env (o dai user-secrets).
// Qualunque casella autenticata da mailcow può scrivere: non c'è un elenco di
// autori da configurare; senza client l'area di scrittura resta spenta.
var mailcowOauthUrl = builder.AddParameter("mailcow-oauth-url",
    () => builder.Configuration["Parameters:mailcow-oauth-url"] ?? "https://mail.maddalena.me");
var mailcowOauthClientId = builder.AddParameter("mailcow-oauth-client-id",
    () => builder.Configuration["Parameters:mailcow-oauth-client-id"] ?? "");
var mailcowOauthClientSecret = builder.AddParameter("mailcow-oauth-client-secret",
    () => builder.Configuration["Parameters:mailcow-oauth-client-secret"] ?? "", secret: true);

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

var site = builder.AddProject<Projects.MainSite>("site")
    .WithEnvironment("Calendar__CalDavUrl", caldavUrl)
    .WithEnvironment("Calendar__Password", caldavPassword)
    .WithEnvironment("Blog__Auth__BaseUrl", mailcowOauthUrl)
    .WithEnvironment("Blog__Auth__ClientId", mailcowOauthClientId)
    .WithEnvironment("Blog__Auth__ClientSecret", mailcowOauthClientSecret)
    .WithEnvironment("RequestLog__Path", requestLogPath)
    .WithEnvironment("RequestLog__GeoIpDatabasePath", geoIpPath)
    // Gli articoli stanno fuori dall'immagine: un deploy che ricostruisce il
    // container non deve portarsi via quello che è stato scritto.
    .WithEnvironment("Blog__ContentPath", blogContentPath)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.AddVolume(new Volume
        {
            Name = "blog-data",
            Type = "volume",
            Source = "blog-data",
            Target = "/data/blog",
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

// --- Caddy: la "portineria" sulle porte 80/443 ------------------------------
// Immagine su misura (Caddy ufficiale + modulo layer4), costruita dal
// Dockerfile in ./caddy. Serve per passare il TLS della posta sigillato a
// mailcow senza decifrarlo.
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
    .WithEnvironment("WEBMAIL_HOST", webmailHost)
    .WithEnvironment("SITE_UPSTREAM", siteUpstream)
    .WithEnvironment("THEQULT_UPSTREAM", thequltUpstream)
    .WithEnvironment("MADDALENA_UPSTREAM", maddalenaUpstream)
    .WithEnvironment("MAILCOW_TLS_UPSTREAM", mailcowTlsUpstream)
    .WithEnvironment("MAILCOW_HTTP_UPSTREAM", mailcowHttpUpstream)
    // Aggancia Caddy alla rete condivisa (per raggiungere mailcow per nome) e
    // rende il compose autosufficiente: lo costruisce dal Dockerfile in ./caddy
    // invece di pretendere un'immagine gia' pronta.
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Networks.Add(SharedNetworkKey);
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
