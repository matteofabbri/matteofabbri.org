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
//   - site-domain  -> dominio del sito personale (es. matteofabbri.org)
//   - webmail-host -> dove vive la webmail, bersaglio del redirect /mail
//                     (es. mail.matteofabbri.org; in transizione il nome OVH)
var siteDomain = builder.AddParameter("site-domain");
var webmailHost = builder.AddParameter("webmail-host");

// Dove Caddy raggiunge mailcow, per nome sulla rete condivisa.
//   - mailcow-tls-upstream  -> nginx di mailcow, porta HTTPS (es. nginx-mailcow:8443)
//   - mailcow-http-upstream -> nginx di mailcow, porta HTTP  (es. nginx-mailcow:8080)
var mailcowTlsUpstream = builder.AddParameter("mailcow-tls-upstream");
var mailcowHttpUpstream = builder.AddParameter("mailcow-http-upstream");

// --- Il sito personale -------------------------------------------------------
var site = builder.AddProject<Projects.MainSite>("site");

// --- Il sito licenze (da un altro repo: SPERONE, via sottomodulo git) --------
// NOTA: questo progetto si aspetta una connection string MongoDB
// (ConnectionStrings:licenses): cosi' com'e' compila e si avvia come risorsa,
// ma per FUNZIONARE va ancora cablato il Mongo (prossimo passo).
builder.AddProject<Projects.Sperone_LicenseSite>("licensesite");

// L'indirizzo interno (host:porta) a cui Caddy inoltra il traffico del sito:
// deriva dall'endpoint del progetto, quindi nel compose diventa da solo
// "site:8080" senza nulla di inchiodato a mano.
var siteHttp = site.GetEndpoint("http");
var siteUpstream = ReferenceExpression.Create(
    $"{siteHttp.Property(EndpointProperty.Host)}:{siteHttp.Property(EndpointProperty.TargetPort)}");

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
    .WithEnvironment("WEBMAIL_HOST", webmailHost)
    .WithEnvironment("SITE_UPSTREAM", siteUpstream)
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
