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
var site = builder.AddProject<Projects.MainSite>("site");

// --- theqult.org: progetto API ----------------------------------------------
var theqult = builder.AddProject<Projects.TheQult>("theqult");

// --- maddalena.me: progetto MVC ---------------------------------------------
// Fa da sito principale del dominio; sotto /licensing ospita il licensing di
// Sperone. Account LOCALI (Identity + cookie): niente piu' provider OIDC.
var maddalena = builder.AddProject<Projects.Maddalena>("maddalena");

// Postgres: gli account di Maddalena (ASP.NET Core Identity) + le licenze
// commerciali comprate (tabella AccountLicense). WithDataVolume => i dati sopravvivono ai riavvii.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

// DB di Maddalena: account + licenze. Il licensing vive ORA dentro Maddalena (sotto
// /licensing) — niente piu' sito separato ne' Mongo.
var maddalenaDb = postgres.AddDatabase("maddalenadb");

// Chiavi Stripe (test/sandbox in dev, live in prod). I valori stanno negli user-secrets di QUESTO AppHost
// come Parameters:stripe-* ; opzionali (default vuoto) cosi' Maddalena parte anche prima di impostarle.
//   dotnet user-secrets set "Parameters:stripe-secret" "sk_test_…" --project <questo AppHost>
var stripePublishable = builder.AddParameter("stripe-publishable", () => builder.Configuration["Parameters:stripe-publishable"] ?? "");
var stripeSecret = builder.AddParameter("stripe-secret", () => builder.Configuration["Parameters:stripe-secret"] ?? "", secret: true);
var stripeWebhook = builder.AddParameter("stripe-webhook", () => builder.Configuration["Parameters:stripe-webhook"] ?? "", secret: true);

// La chiave PRIVATA di firma delle licenze: vive SOLO qui (mai nel prodotto). Il webhook la usa per firmare
// la chiave portabile. Vuota di default (Maddalena parte lo stesso); in prod via user-secrets.
//   dotnet user-secrets set "Parameters:licensing-private-key" "<base64>" --project <questo AppHost>
var licensingPrivateKey = builder.AddParameter("licensing-private-key",
    () => builder.Configuration["Parameters:licensing-private-key"] ?? "", secret: true);

// Maddalena ospita il licensing: le servono il suo DB, le chiavi Stripe e la chiave
// privata di firma (Stripe__X -> Stripe:X ; Licensing__X -> Licensing:X).
maddalena
    .WithReference(maddalenaDb)
    .WaitFor(maddalenaDb)
    .WithEnvironment("Stripe__PublishableKey", stripePublishable)
    .WithEnvironment("Stripe__SecretKey", stripeSecret)
    .WithEnvironment("Stripe__WebhookSecret", stripeWebhook)
    .WithEnvironment("Licensing__PrivateKey", licensingPrivateKey)
    .WithEnvironment("Licensing__KeyId", "k1");

// --- Mappe: i tiles -----------------------------------------------------------
// tileserver-gl serve l'IMMAGINE della mappa sotto maddalena.me/tiles.
// Resta interno: fuori ci arriva solo Caddy.

// Quale mappa del mondo scaricare. Vuoto = l'ultima pubblicata: Protomaps ne
// sforna una al giorno col nome fatto di data (20260731.pmtiles) e tiene online
// solo l'ultima settimana, quindi un indirizzo fisso "l'ultimo" non esiste e lo
// script d'avvio va a ritroso dall'oggi finche' non ne trova uno. Si valorizza
// nel .env solo per inchiodare una versione precisa.
var planetUrl = builder.AddParameter("planet-url",
    () => builder.Configuration["Parameters:planet-url"] ?? "");

// A differenza dell'immagine ufficiale, questa si SCARICA DA SOLA la mappa del
// mondo (~137 GB) e i font al primo avvio, dentro il volume: vedi
// ./tileserver/prepara-mappe.sh. Nessun file da portare a mano sul server.
// Il primo avvio dura quanto il download; nel frattempo /tiles risponde 502.
var tileserver = builder.AddDockerfile("tileserver", "tileserver")
    // La mappa scaricata. Senza volume ogni riavvio rifarebbe i 137 GB.
    .WithVolume("tileserver-data", "/data")
    .WithEndpoint(targetPort: 8080, scheme: "http", name: "http")
    .WithEnvironment("PLANET_URL", planetUrl)
    // Indispensabile dietro a un prefisso di percorso: tileserver-gl scrive gli
    // indirizzi delle tiles DENTRO il JSON dello stile che serve. Senza questo
    // li scriverebbe come /data/... e il client li chiederebbe alla radice del
    // dominio, dove non c'e' niente.
    .WithArgs("--public_url", ReferenceExpression.Create($"https://{maddalenaDomain}/tiles/"))
    // Come per Caddy: senza questo il compose uscirebbe con
    // `image: ${TILESERVER_IMAGE}` — cioe' PRETENDEREBBE un'immagine gia'
    // costruita altrove, e sul server (dove quella variabile e' vuota) il
    // container non partirebbe affatto. E' il comportamento di default di
    // Aspire per AddDockerfile; va corretto a mano per ogni immagine su misura.
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Image = null;
        service.Build = new Build
        {
            Context = "./tileserver",
            Dockerfile = "Dockerfile",
        };
    });

// --- Ricerca di luoghi per nome ---------------------------------------------
// Il pezzo che le mappe non sanno fare: DISEGNANO Madrid, ma non sanno
// TROVARLA partendo dalla parola "madrid".
// Senza, un'app puo' cercare solo fra le tessere che ha gia' scaricato, cioe'
// quelle intorno a dove si trova: Grenoble a 267 km si', Madrid a 1400 no.
// Sono ~170.000 centri abitati da GeoNames in uno SQLite: 10 MB, si scarica da
// solo al primo avvio come fa il tileserver.
var luoghi = builder.AddDockerfile("luoghi", "luoghi")
    .WithVolume("luoghi-dati", "/dati")
    .WithEndpoint(targetPort: 8090, scheme: "http", name: "http")
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Image = null;
        service.Build = new Build
        {
            Context = "./luoghi",
            Dockerfile = "Dockerfile",
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
var tileserverUpstream = ReferenceExpression.Create(
    $"{tileserver.GetEndpoint("http").Property(EndpointProperty.HostAndPort)}");
var luoghiUpstream = ReferenceExpression.Create(
    $"{luoghi.GetEndpoint("http").Property(EndpointProperty.HostAndPort)}");

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
    .WithEnvironment("TILESERVER_UPSTREAM", tileserverUpstream)
    .WithEnvironment("LUOGHI_UPSTREAM", luoghiUpstream)
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

builder.Build().Run();
