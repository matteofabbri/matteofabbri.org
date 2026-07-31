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

// --- Mappe: calcolo percorsi e tiles ----------------------------------------
// Due servizi distinti, e la distinzione conta: Valhalla calcola i PERCORSI,
// tileserver-gl serve l'IMMAGINE della mappa. Nessuno dei due fa il lavoro
// dell'altro. Vivono sotto maddalena.me: /osm il primo, /tiles il secondo.
// Entrambi restano interni: fuori ci arriva solo Caddy.

// Da dove Valhalla prende i dati OpenStreetMap. Al primo avvio scarica questo
// estratto e ne costruisce il grafo di navigazione: e' un'operazione LUNGA
// (decine di minuti per l'Italia) e affamata di RAM, ma succede una volta sola
// perche' il risultato resta nel volume. Cambiare regione = cambiare questo
// parametro nel .env e riavviare con force_rebuild a True.
var valhallaTileUrls = builder.AddParameter("valhalla-tile-urls",
    () => builder.Configuration["Parameters:valhalla-tile-urls"]
        ?? "https://download.geofabrik.de/europe/italy-latest.osm.pbf");

var valhalla = builder.AddContainer("valhalla", "ghcr.io/nilsnolde/docker-valhalla/valhalla", "3.5.1")
    // Il grafo costruito. Senza volume ogni riavvio rifarebbe da capo il lavoro
    // di cui sopra.
    .WithVolume("valhalla-tiles", "/custom_files")
    .WithEndpoint(targetPort: 8002, scheme: "http", name: "http")
    .WithEnvironment("tile_urls", valhallaTileUrls)
    // Serve appena il grafo e' pronto, e non lo ricostruisce a ogni avvio.
    .WithEnvironment("serve_tiles", "True")
    .WithEnvironment("force_rebuild", "False")
    // Confini amministrativi e fusi orari: servono per le istruzioni di svolta
    // ("entri in Francia") e per gli orari di arrivo corretti. Costano tempo di
    // costruzione una volta sola.
    .WithEnvironment("build_admins", "True")
    .WithEnvironment("build_time_zones", "True")
    // L'altimetria no: pesa parecchi GB e serve solo a ciclismo e pedonale.
    .WithEnvironment("build_elevation", "False");

// Quale mappa del mondo scaricare. Vuoto = l'ultima pubblicata: Protomaps ne
// sforna una al giorno col nome fatto di data (20260731.pmtiles) e tiene online
// solo l'ultima settimana, quindi un indirizzo fisso "l'ultimo" non esiste e lo
// script d'avvio va a ritroso dall'oggi finche' non ne trova uno. Si valorizza
// nel .env solo per inchiodare una versione precisa.
var planetUrl = builder.AddParameter("planet-url",
    () => builder.Configuration["Parameters:planet-url"] ?? "");

// A differenza dell'immagine ufficiale, questa si SCARICA DA SOLA la mappa del
// mondo (~137 GB) e i font al primo avvio, dentro il volume: vedi
// ./tileserver/prepara-mappe.sh. Nessun file da portare a mano sul server, come
// gia' fa Valhalla coi suoi dati. Il primo avvio dura quanto il download; nel
// frattempo /tiles risponde 502.
var tileserver = builder.AddDockerfile("tileserver", "tileserver")
    // La mappa scaricata. Senza volume ogni riavvio rifarebbe i 137 GB.
    .WithVolume("tileserver-data", "/data")
    .WithEndpoint(targetPort: 8080, scheme: "http", name: "http")
    .WithEnvironment("PLANET_URL", planetUrl)
    // Indispensabile dietro a un prefisso di percorso: tileserver-gl scrive gli
    // indirizzi delle tiles DENTRO il JSON dello stile che serve. Senza questo
    // li scriverebbe come /data/... e il client li chiederebbe alla radice del
    // dominio, dove non c'e' niente.
    .WithArgs("--public_url", ReferenceExpression.Create($"https://{maddalenaDomain}/tiles/"));

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
var valhallaUpstream = ReferenceExpression.Create(
    $"{valhalla.GetEndpoint("http").Property(EndpointProperty.HostAndPort)}");
var tileserverUpstream = ReferenceExpression.Create(
    $"{tileserver.GetEndpoint("http").Property(EndpointProperty.HostAndPort)}");

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
    .WithEnvironment("VALHALLA_UPSTREAM", valhallaUpstream)
    .WithEnvironment("TILESERVER_UPSTREAM", tileserverUpstream)
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
