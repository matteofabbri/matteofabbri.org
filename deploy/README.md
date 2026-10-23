# Deploy — sito + mailcow dietro Caddy

Due stack docker separati sulla stessa macchina, collegati da una rete condivisa
"neutra" (`matteo-shared`). Caddy fa da portineria sulle porte 80/443 e smista
per nome:

- `matteofabbri.org` (`SITE_DOMAIN`) → sito personale (progetto `site`)
- `theqult.org` (`THEQULT_DOMAIN`)   → API `TheQult`
- `maddalena.me` (`MADDALENA_DOMAIN`) → sito `Maddalena` (per ora un hello world)
- `maddalena.me/tiles`               → tileserver-gl, immagine della mappa
- `<dominio>/mail` (tutti e tre)     → redirect a `https://<WEBMAIL_HOST>`
- **qualunque altro nome**           → mailcow (TLS **passato sigillato**: il
  certificato se lo fa mailcow)

Tutti e tre i SITI hanno il TLS terminato da Caddy (certificati Let's Encrypt).

La regola d'oro: Caddy conosce solo i SITI. I nomi della posta
(`mail.maddalena.me`, domini futuri) NON vanno dichiarati in Caddy: ci passano
da soli. Per quelli si lavora solo lato mailcow e DNS (vedi sezione 4).
Unica eccezione, dichiarata: `autoconfig.maddalena.me` e
`autodiscover.maddalena.me` hanno un forward esplicito verso mailcow nel
Caddyfile (matcher `@mailauto`, sia su 443 che su 80) — sempre in passthrough,
il certificato resta di mailcow.

Le porte della posta vera (SMTP 25/465/587, IMAP 143/993, POP 110/995, sieve
4190) le pubblica mailcow direttamente verso l'esterno: NON passano da Caddy.
Lista completa porte da aprire/non aprire (+ note OVH): [porte.md](porte.md).

---

## 0. Una volta sola: la rete condivisa

```bash
docker network create matteo-shared
```

È "di nessuno": così quando aggiorni mailcow il corridoio resta in piedi.

---

## 1. mailcow (stack separato, suo ciclo di vita)

Nel file `mailcow.conf` (generato da `generate_config.sh`):

```ini
# Per ORA il nuovo mailcow serve solo maddalena.me/theqult.org: la posta di
# matteofabbri.org resta sul vecchio server, DNS incluso (nessuna collisione:
# mail.maddalena.me e' un sottodominio nuovo, non ancora usato da nessuno).
MAILCOW_HOSTNAME=mail.maddalena.me

# Caddy tiene 80/443 sull'host: mailcow ascolta su porte interne e si pubblica
# solo su loopback, così non va in conflitto. Caddy lo raggiunge comunque per
# nome sulla rete condivisa (nginx-mailcow:8080 / :8443).
HTTP_PORT=8080
HTTP_BIND=127.0.0.1
HTTPS_PORT=8443
HTTPS_BIND=127.0.0.1

# Let's Encrypt resta ATTIVO: mailcow si fa il suo certificato (la sfida HTTP-01
# entra dalla porta 80 e Caddy la inoltra a mailcow). NON impostare SKIP_LETS_ENCRYPT.
```

Copia l'override accanto al `docker-compose.yml` di mailcow:

```bash
cp deploy/mailcow/docker-compose.override.yml /percorso/mailcow-dockerized/docker-compose.override.yml
```

Avvia mailcow:

```bash
cd /percorso/mailcow-dockerized
docker compose up -d
```

> L'override affaccia `nginx-mailcow` e `postfix-mailcow` sulla rete condivisa.
> Scommenta `dovecot-mailcow` nell'override se il sito deve anche LEGGERE le
> caselle via IMAP (oltre a inviare).

---

## 2. Stack Aspire (sito + Caddy)

Genera il compose con lo script (fa `aspire publish` + copia il contesto di
build di Caddy, che la publish da sola non porta con se'):

```powershell
cd MatteoFabbri.org
.\publish.ps1          # output in .\out (oppure: .\publish.ps1 -Output X)
```

Compila il file `out/.env` con i valori reali:

```ini
ACME_EMAIL=tua@mail.it
SITE_DOMAIN=matteofabbri.org             # dominio del sito (Caddy lo termina lui)
THEQULT_DOMAIN=theqult.org               # secondo sito (Caddy lo termina lui)
MADDALENA_DOMAIN=maddalena.me            # terzo sito (Caddy lo termina lui)
WEBMAIL_HOST=mail.maddalena.me           # bersaglio del redirect /mail (per ora: solo maddalena.me/theqult.org)
SITE_IMAGE=matteofabbri-site:latest       # immagine del sito (la fa publish.ps1)
SITE_PORT=8080                            # porta interna del sito
THEQULT_IMAGE=matteofabbri-theqult:latest # immagine di TheQult (la fa publish.ps1)
THEQULT_PORT=8080                         # porta interna di TheQult
MADDALENA_IMAGE=matteofabbri-maddalena:latest # immagine di Maddalena (la fa publish.ps1)
MADDALENA_PORT=8080                       # porta interna di Maddalena
MAILCOW_TLS_UPSTREAM=nginx-mailcow:8443   # nginx di mailcow, HTTPS
MAILCOW_HTTP_UPSTREAM=nginx-mailcow:8080  # nginx di mailcow, HTTP

# Prenotazioni (calendario CalDAV su SOGo): vedi la sezione 2-quater.
# Vuote = funzione spenta (l'API risponde 503), il sito parte lo stesso.
CALDAV_URL=https://mail.maddalena.me/SOGo/dav/
CALDAV_USERNAME=matteo@maddalena.me      # l'indirizzo della casella
CALDAV_PASSWORD=                         # APP PASSWORD con accesso DAV, NON la password vera

# Blog (accesso con mailcow via OAuth2): vedi la sezione 2-quinquies.
# Vuote = area di scrittura spenta, il blog resta leggibile da tutti.
MAILCOW_OAUTH_URL=https://mail.maddalena.me
MAILCOW_OAUTH_CLIENT_ID=                 # da mailcow: Configuration → Access → OAuth2 Apps
MAILCOW_OAUTH_CLIENT_SECRET=             # generato da mailcow insieme al client
BLOG_AUTHORS=matteo@maddalena.me         # caselle abilitate a scrivere, separate da virgola
```

> `THEQULT_DOMAIN`/`MADDALENA_DOMAIN` hanno gia' un default nell'AppHost
> (`theqult.org`/`maddalena.me`): nel `.env` servono solo se vuoi cambiarli.

Avvia (la prima volta costruisce l'immagine Caddy su misura, col modulo layer4):

```bash
cd out
docker compose up -d --build
```

---

## 2-bis. Le mappe: tileserver-gl

tileserver-gl serve l'immagine della mappa su `maddalena.me/tiles` e resta
interno: fuori ci arriva solo Caddy.

Non c'è niente da portare a mano. L'immagine è nostra (contesto
`MatteoFabbri.org.AppHost/tileserver`, costruita sul server dal `--build`): al
primo avvio, se il volume `tileserver-data` è vuoto, scarica da sé la **mappa
del mondo di Protomaps** — circa **137 GB**, zoom 0-15, licenza ODbL — più i
font per le etichette (~19 MB). Poi parte. Ai riavvii trova tutto e parte
subito.

Il primo avvio dura quanto il download. Nel frattempo `/tiles` risponde 502: è
normale, non è rotto. Se il container viene fermato a metà, il download
**riprende da dove era** (non ricomincia).

Come va: `sudo docker compose logs -f tileserver` — le righe sono marcate
`[mappe]`.

**Quale build.** Protomaps ne pubblica uno al giorno, il nome è la data
(`20260731.pmtiles`), e tiene online solo l'ultima settimana: un indirizzo
fisso "l'ultimo" non esiste. Lo script parte da oggi e va a ritroso finché non
ne trova uno che risponde. Per inchiodare una versione precisa, nel `.env`:

```ini
PLANET_URL=https://build.protomaps.com/20260731.pmtiles
```

Lasciato vuoto = sempre l'ultimo disponibile al momento del primo avvio.

**Per aggiornare la mappa** (i dati OpenStreetMap invecchiano): cancella il
file e riavvia, si riscarica l'ultimo.

```bash
sudo docker compose stop tileserver
sudo docker run --rm -v matteofabbri_tileserver-data:/data alpine rm -f /data/planet.pmtiles
sudo docker compose start tileserver
```

Prova che funzioni:

```bash
curl -s "https://maddalena.me/tiles/data/planet.json" | head -c 300
```

**Cosa serve il server e cosa no.** Serve i **dati** (le tessere) e i **font**
delle etichette:

- tessere: `/tiles/data/planet/{z}/{x}/{y}.pbf` — il contratto sta in
  `/tiles/data/planet.json`
- font: `/tiles/fonts/{nome}/{intervallo}.pbf`, per esempio
  `/tiles/fonts/Noto%20Sans%20Medium/0-255.pbf`

Non serve lo **stile** (l'aspetto grafico: colori, spessori, quali etichette a
quale zoom): quello lo porta il frontend, dove appartiene, insieme alle icone
dei punti d'interesse. Lo stile va preso dai temi Protomaps — `light`, `dark`,
`white`, `black`, `grayscale` — e dentro vanno puntati gli indirizzi qui sopra
al posto di quelli del CDN pubblico.

> Dettaglio che è costato una mezz'ora di prove, per chi ci rimetterà mano:
> nel `config.json` generato c'è `"serveAllFonts": true`, e non è decorativo.
> Senza, tileserver-gl serve solo i font citati da uno stile che conosce — e
> stili non ne ha — quindi ogni font risponde `400 Error serving font` e la
> mappa esce **senza una scritta**. L'immagine ufficiale, fra l'altro, porta un
> solo font dei quattro che lo stile Protomaps usa: gli altri tre arrivano col
> download dei ~19 MB.

> `--public_url` è impostato dall'AppHost a `https://<MADDALENA_DOMAIN>/tiles/`
> e non è un dettaglio: tileserver-gl scrive gli indirizzi delle tiles **dentro**
> il JSON dello stile che serve, in forma assoluta. Se non concorda col percorso
> reale, il client chiede le tiles alla radice del dominio e trova il sito
> Maddalena invece della mappa.

---

## 2-ter. Togliere un servizio: la pulizia di docker

Quando un servizio esce di scena, sul server restano i suoi avanzi: container
fermo, volume con dentro i dati, immagine costruita. La pulizia è **generica** —
cerca per label di progetto compose, senza nomi di servizi scritti da nessuna
parte — ed è cablata in due posti:

- **deploy GitHub**: a ogni deploy, dopo `docker compose up -d --build
  --remove-orphans`, rimuove volumi e immagini dello stack `matteofabbri` che
  nessun container usa più (vedi il passo "Riavvio container e pulizia" in
  `.github/workflows/deploy.yml`);
- **`server-setup.sh`**: la stessa pulizia, se lo rilanci a mano sul server.

**Il passaggio che il workflow NON fa per te**: il deploy non aggiorna mai il
`docker-compose.yaml` né il `.env` sul server (contengono segreti e cambiano di
rado). La pulizia agisce solo su ciò che il compose *definisce*: finché il
compose sul server contiene ancora il servizio, nulla viene buttato. Quindi,
per togliere un servizio davvero:

1. togli il servizio dall'AppHost e ricrea il compose con `.\publish.ps1`;
2. copia `out\docker-compose.yaml` su `/opt/matteofabbri/`, a mano come sempre;
3. dal deploy successivo (o subito, con `sudo docker compose up -d --build
   --remove-orphans`) container, volumi e immagini del servizio spariscono da
   soli;
4. facoltativo: nel `.env` togli le variabili ormai ignorate
   (`VALHALLA_TILE_URLS`, `POSTGRES_PASSWORD`, ...).

> **ATTENZIONE ai dati.** Un volume orfano contiene i *dati* del servizio
> tolto: nel volume di postgres (`...-postgres-data`, vedi `docker volume ls |
> grep postgres`) ci sono gli account e le licenze di Maddalena, e nel volume
> `matteofabbri_blog-data` ci sono **tutti gli articoli e le immagini del
> blog**. La pulizia li cancella per sempre, senza chiedere conferma. Se pensi
> che possano ancora servire, salvali PRIMA di aggiornare il compose sul
> server:
>
> ```bash
> sudo docker run --rm -v NOME_VOLUME:/dati -v "$(pwd)":/backup alpine \
>   tar czf /backup/backup-$(date +%F).tar.gz -C /dati .
> ```

La pulizia non può sbagliare bersaglio fuori casa propria: i volumi di mailcow
non hanno la label di questo progetto, e la rete `matteo-shared` è esterna al
compose quindi resta in piedi comunque.

---

## 2-quater. Prenotazioni: il calendario di mailcow (CalDAV)

Le API vivono in **MainSite** (matteofabbri.org), sotto `/api/booking/`. Leggono
e scrivono il calendario della casella su **SOGo**, il groupware che mailcow
include: mailcow non ha un calendario "suo", ma SOGo espone quello di ogni
casella via CalDAV, ed è l'unico modo per arrivarci.

```
GET  /api/booking/availability?from=2026-11-01&to=2026-11-30
POST /api/booking/appointments
```

### Le credenziali: un'app password, non la password vera

SOGo accetta Basic auth con **email + app password**. Le password vere
cambiano, e con 2FA un client DAV non funziona affatto.

1. entra nella webmail (`https://mail.maddalena.me`) con la casella che vuoi
   rendere prenotabile;
2. **Impostazioni → App password → Crea**: nome (`prenotazioni`), protocollo
   **DAV** abilitato (bastano DAV; non serve IMAP/SMTP a questo scopo);
3. copia la password generata: si vede una volta sola.

Poi, in sviluppo (mai nel repo):

```powershell
dotnet user-secrets set "Parameters:caldav-username" "matteo@maddalena.me" --project MatteoFabbri.org.AppHost
dotnet user-secrets set "Parameters:caldav-password" "<app password DAV>" --project MatteoFabbri.org.AppHost
```

Su server: `CALDAV_USERNAME` e `CALDAV_PASSWORD` nel `.env` dell'AppHost (vedi
sezione 2). Se sono vuote le prenotazioni sono spente (503) e il sito parte
lo stesso — un `.env` incompleto non deve buttare giù i siti.

> L'URL è `https://mail.maddalena.me/SOGo/dav/` e il percorso del calendario è
> `Calendar/personal/` (insieme fanno
> `.../SOGo/dav/matteo@maddalena.me/Calendar/personal/`). Se un domani vuoi un
> calendario separato ("Appuntamenti") per non mescolare gli impegni privati,
> crealo in SOGo e metti il suo percorso in `Calendar:CalendarPath`.

### Come si decide cosa è prenotabile

Le regole stanno in `MainSite/appsettings.json`, sezione `Booking` (si possono
sovrascrivere da ambiente con `Booking__SlotMinutes`, `Booking__WorkStart`, ...):

| Regola | Default | Nota |
|---|---|---|
| fuso | `Europe/Rome` | gli slot escono con l'offset reale (+01:00/+02:00) |
| giorni | lun–ven | ISO (`1` = lunedì) |
| orario | 09:00–18:00 | |
| slot | 30 min | è anche il passo della griglia |
| buffer | 15 min | libero prima e dopo ogni impegno già in calendario |
| preavviso | 24 h | niente "tra un'ora" |
| orizzonte | 60 giorni | oltre, niente slot |
| giorni per richiesta | 14 (max 31) | il cliente può chiedere un intervallo |

Gli impegni si leggono dal calendario con una `REPORT calendar-query` (ricorrenze
incluse, espanse da Ical.Net); `TRANSP:TRANSPARENT` e gli eventi annullati non
contano. **Dei tuoi impegni l'API non espone mai nulla**: restituisce solo gli
slot liberi.

La stessa funzione che pubblica la disponibilità valida la prenotazione, quindi
le due cose non possono divergere. La scrittura è un `PUT` del `.ics` con
`If-None-Match: *`: il nome del file deriva dallo slot, così due prenotazioni
dello stesso orario **collidono (409) invece di sovrascriversi**. Il titolo
dell'evento è `Appuntamento con <nome>`, la descrizione contiene email e
messaggio del richiedente.

### Esempi

```bash
curl "https://matteofabbri.org/api/booking/availability?from=2026-11-02&to=2026-11-06"
```

```json
{
  "timeZone": "Europe/Rome",
  "slotMinutes": 30,
  "days": [
    { "date": "2026-11-02", "slots": [
        { "start": "2026-11-02T09:00:00+01:00", "end": "2026-11-02T09:30:00+01:00" }
    ] }
  ]
}
```

```bash
curl -X POST https://matteofabbri.org/api/booking/appointments \
  -H "Content-Type: application/json" \
  -d '{"start":"2026-11-02T09:00:00+01:00","name":"Mario Rossi","email":"mario@example.com","note":"Di cosa parliamo"}'
```

Risposte: `201` con l'id; `400` dati non validi; `409` slot non più libero;
`429` troppe richieste (5 in 10 minuti per IP); `503` prenotazioni non
configurate; `502` calendario irraggiungibile o credenziali rifiutate.

### Sviluppo locale senza credenziali

`Calendar:UseFake` = `true` (o `Calendar__UseFake=true`) monta un calendario in
memoria con due impegni di esempio: si possono provare disponibilità e
prenotazione senza toccare il server. **Mai in produzione**: le prenotazioni
non finirebbero da nessuna parte.

> Non c'è ancora l'email di conferma al richiedente: l'appuntamento compare nel
> calendario, e la risposta dell'API basta al sito per mostrare la conferma. Se
> serve, il passo successivo è un invio da mailcow (SMTP della stessa casella).

---

## 2-quinquies. Blog: accesso con mailcow (OAuth2)

Il blog vive in **MainSite**:

| Percorso | Cosa |
|---|---|
| `/blog` | indice pubblico |
| `/blog/{slug}` | un articolo |
| `/blog/tag/{tag}` | articoli con un tag |
| `/blog/feed.xml` | feed RSS |
| `/sitemap.xml` | sitemap del blog |
| `/admin/login` | accesso autori |
| `/admin` | elenco, editor, immagini |

Gli articoli sono file **Markdown con front matter YAML**. In produzione stanno
nel volume Docker `blog-data`, montato in `/data/blog`: un deploy ricostruisce
il container ma non tocca il volume. Non c'è nessun database da amministrare.

### Perché OAuth2 (e non una password)

mailcow include un server OAuth2 (`/oauth/authorize`, `/oauth/token`,
`/oauth/profile`). Il sito manda l'autore sulla webmail, riceve un codice, lo
scambia con un token, legge il profilo della casella e crea il proprio cookie.
La password della casella **non passa mai dal sito**, e il 2FA di mailcow
continua a funzionare. Non serve un'app password: quella del CalDAV (sezione
2-quater) è un'altra cosa.

mailcow dice *chi sei*; il blog decide *se puoi scrivere*: solo le caselle in
`BLOG_AUTHORS` entrano. Gli account amministratore di mailcow **non** sono
caselle e non possono accedere al blog.

### Configurazione (una volta sola)

1. entra in mailcow come **amministratore**;
2. **Configuration → Access → OAuth2 Apps → Add OAuth2 client** (se la voce
   non c'è, aggiorna mailcow: vedi "Aggiornare mailcow" in fondo);
3. come Redirect URI metti esattamente:

   ```
   https://matteofabbri.org/admin/login/callback
   ```

   niente slash finale e niente `http`; se il sito usa un altro dominio, usa
   quello (`SITE_DOMAIN`);
4. salva **Client ID** e **Client Secret** (il secret si vede al momento della
   creazione);
5. nel `.env` dello stack (sezione 2) aggiungi:

   ```ini
   MAILCOW_OAUTH_URL=https://mail.maddalena.me
   MAILCOW_OAUTH_CLIENT_ID=...
   MAILCOW_OAUTH_CLIENT_SECRET=...
   BLOG_AUTHORS=matteo@maddalena.me
   ```

   più caselle separate da virgola. Se `BLOG_AUTHORS` è vuoto l'area di
   scrittura è spenta e il blog resta leggibile da tutti. Metti l'indirizzo
   della **casella** (lo username che mailcow restituisce nel profilo), non un
   alias che punta ad essa: l'alias non comparirebbe mai tra gli autorizzati;
6. aggiorna il compose sul server: il blog aggiunge il **volume** `blog-data` e
   le variabili qui sopra, quindi il deploy automatico da solo non basta.
   Rigenera e copia il compose:

   ```powershell
   .\publish.ps1
   scp out\docker-compose.yaml root@IP_SERVER:/opt/matteofabbri/
   ```

   poi sul server:

   ```bash
   cd /opt/matteofabbri && sudo docker compose up -d --build
   ```

   Il volume `matteofabbri_blog-data` viene creato al primo avvio e conserva gli
   articoli anche ai deploy successivi;
7. apri `https://matteofabbri.org/admin/login` → **Accedi con mailcow**. La
   prima volta mailcow chiede di autorizzare l'app.

### Scrivere e pubblicare

Da `/admin` crei un articolo, ne scegli lo slug (se lo lasci vuoto lo ricaviamo
dal titolo) e scrivi in Markdown: titoli, elenchi, link, tabelle, blocchi di
codice e immagini. L'HTML grezzo non viene eseguito.

La **data di pubblicazione** si interpreta nel fuso `Blog:TimeZone`
(`Europe/Rome`). Un articolo con la spunta **Bozza**, o con una data futura, non
è pubblico: lo vedono solo gli autori autenticati, con un avviso in cima. Il
feed e la sitemap contengono solo gli articoli pubblicati.

Le immagini si caricano da `/admin/media`: finiscono in `media/` dentro il
volume, si servono da `/blog/media/{file}` e il testo da incollare nell'articolo
è già accanto a ogni immagine. Formati ammessi: JPG, PNG, GIF, WebP, AVIF (max
8 MB). Niente SVG.

**Cestino.** Eliminare un articolo non cancella il file: lo sposta in
`_trash/` dentro il volume. Se la cancellazione è stata un errore, si recupera
da lì.

### Backup degli articoli

```bash
sudo docker run --rm -v matteofabbri_blog-data:/dati -v "$(pwd)":/backup alpine \
  tar czf /backup/blog-$(date +%F).tar.gz -C /dati .
```

### Sviluppo locale

`appsettings.Development.json` imposta `Blog:Auth:UseFake=true`: in locale si
entra in `/admin/login` come primo autore configurato, senza mailcow. **In
produzione l'accesso finto viene ignorato** anche se impostato. Il vero OAuth
in locale richiederebbe HTTPS (il cookie di correlazione è `Secure`); per
provare il giro completo conviene usare lo stack reale.

### Se l'accesso non funziona

- **"Accesso non configurato"**: mancano le variabili nel `.env` o il container
  non è stato ricreato dopo aver aggiornato il compose.
- **"Accesso non riuscito"**: redirect URI diverso da quello registrato, client
  ID/secret sbagliati, casella non presente in `BLOG_AUTHORS`, oppure mailcow
  irraggiungibile dal sito. Log: `sudo docker compose logs site`.
- **La casella è giusta ma non entra**: controlla che sia una casella, non un
  account admin di mailcow; e che l'indirizzo in `BLOG_AUTHORS` sia quello
  della casella (username), completo di dominio (`nome@dominio`), non un alias.

---

## 3. DNS (perché certificati e posta funzionino)

Scelta corrente: il nuovo mailcow serve **solo maddalena.me/theqult.org**.
**matteofabbri.org non si tocca** — resta sul suo server di posta attuale.

| Record | Nome | Valore |
|--------|------|--------|
| A | `matteofabbri.org` | IP del server (sito, non posta) |
| A | `theqult.org` | IP del server (sito) |
| A | `maddalena.me` | IP del server (sito) |
| A | `mail.maddalena.me` | IP del server |
| A | `autodiscover.maddalena.me` | IP del server |
| A | `autoconfig.maddalena.me` | IP del server |
| MX | `maddalena.me` | `mail.maddalena.me` |
| MX | `theqult.org` | `mail.maddalena.me` |
| PTR (reverse) | IP del server | `mail.maddalena.me` |
| TXT (SPF/DKIM/DMARC) | maddalena.me, theqult.org | come indicato dal pannello mailcow |

Il reverse DNS (PTR) che punta a `mail.maddalena.me` è importante per non
finire in spam: si imposta dal pannello del provider della macchina.

Su matteofabbri.org non aggiungere/cambiare nulla lato posta (niente MX,
niente autodiscover/autoconfig, niente PTR): il record A del sito è l'unico
che lo riguarda qui.

---

## 4. Più domini e un'eventuale futura migrazione di matteofabbri.org

### Aggiungere un altro dominio di posta (oltre maddalena.me/theqult.org)

Solo pannello admin di mailcow ("Configurazione e dettagli > Domini > Aggiungi
dominio") + il suo MX -> `mail.maddalena.me` + TXT (SPF/DKIM/DMARC) come
indicato dal pannello. I client si connettono sempre a `mail.maddalena.me`
(un server, molti domini). Caddy non si tocca.

### Se un domani vorrai spostare qui anche la posta di matteofabbri.org

Basta cambiare l'MX di matteofabbri.org da quello attuale a
`mail.maddalena.me`, aggiungere il dominio in mailcow e impostare SPF/DKIM/DMARC
dal pannello. Non serve nessun trucco di hostname provvisorio: `mail.maddalena.me`
non va in conflitto con nulla del vecchio server di posta, quindi puoi testare
l'invio con calma anche PRIMA di girare l'MX definitivo (manda una mail di
prova in uscita per verificare che la porta 25 sia sbloccata sul nuovo VPS,
vedi [porte.md](porte.md)).

### Un sito web su un altro dominio

`matteofabbri.org`, `theqult.org` e `maddalena.me` sono già cablati in Caddy.
Per i loro certificati basta il record A → IP del server (Caddy fa il resto).

Per un **quarto** dominio (è un sito, non posta, quindi tocca Caddy):
1. nell'`AppHost.cs`: aggiungi il parametro del dominio e la sua `XXX_UPSTREAM`,
   sul modello di `theqult-domain`/`THEQULT_UPSTREAM`;
2. nel `Caddyfile`: aggiungi `{$XXX_DOMAIN}` alle due righe `@site` (:443 e :80)
   e crea il blocco del sito in fondo (c'è l'esempio);
3. ricostruisci: `docker compose up -d --build`;
4. record A del dominio → IP del server. Il certificato lo fa Caddy da solo.

---

## 5. Server nuovo da zero (bootstrap automatico)

C'è uno script che fa tutto sul server Linux (Debian/Ubuntu): installa docker,
crea la rete condivisa, configura e avvia mailcow, avvia lo stack del sito.

Dal PC (PowerShell), prepara e copia tutto:

```powershell
cd MatteoFabbri.org
.\publish.ps1                                              # genera .\out (compose + tutte e tre le immagini)
docker save matteofabbri-site:latest -o out\matteofabbri-site.tar
docker save matteofabbri-theqult:latest -o out\matteofabbri-theqult.tar
docker save matteofabbri-maddalena:latest -o out\matteofabbri-maddalena.tar
# COMPILA out\.env PRIMA di copiarlo (vedi sezione 2)
scp -r out deploy root@IP_SERVER:~/
```

Sul server:

```bash
sudo bash ~/deploy/server-setup.sh
```

Lo script è rilanciabile: se un passo è già fatto lo salta. L'unico momento
interattivo è la prima configurazione di mailcow (`generate_config.sh`), che
chiede l'hostname: rispondi `mail.maddalena.me`. Se il `.env` è incompleto
lo script si ferma e ti dice cosa compilare.

Per gli aggiornamenti successivi (i tre siti insieme; salta quelli che non sono cambiati):

```powershell
.\publish.ps1
docker save matteofabbri-site:latest matteofabbri-theqult:latest matteofabbri-maddalena:latest | ssh root@IP_SERVER docker load
scp out\docker-compose.yaml root@IP_SERVER:/opt/matteofabbri/
ssh root@IP_SERVER 'cd /opt/matteofabbri && docker compose up -d --build --remove-orphans'
```

---

## Ordine consigliato (se fai a mano)

1. `docker network create matteo-shared`
2. mailcow su (prende il suo certificato via Caddy:80)
3. stack Aspire su
4. configura il DNS e verifica i certificati

## Aggiornare mailcow

Sul server (installazione in `/opt/mailcow-dockerized`, vedi `server-setup.sh`).

Prima di partire, controlla lo spazio: l'update scarica immagini nuove e su
questa macchina il disco pieno di agosto ha corrotto tre container di mailcow.

```bash
df -h /
```

Con parecchi GB liberi si va tranquilli; sotto i ~10 GB meglio fare ordine
prima (le mappe occupano tanto).

```bash
cd /opt/mailcow-dockerized
sudo ./update.sh
```

Lo script chiede il ramo (default = stabile: Invio), poi fa da solo `git pull`,
`docker compose pull` e `up -d`. Se alla fine propone di rimuovere le immagini
vecchie, `y` libera disco (con `N` restano per un rollback). Si aggiorna solo
da terminale con `update.sh`, mai dal pannello web. Durante l'aggiornamento la
webmail sta giù qualche minuto: la posta in arrivo non si perde, i server
mittenti riprovano da soli.

Verifica a fine aggiornamento: `sudo docker compose ps` (tutti Up; i container
partono in ordine, dai 1-2 minuti) e la webmail su `https://mail.maddalena.me`.

Lo stack Aspire non si tocca: progetto diverso, rete condivisa intatta. Anche
`mailcow.conf` e `docker-compose.override.yml` (l'aggancio a `matteo-shared`)
non vengono toccati: non sono tracciati da git, e la rete è `external: true`
quindi il compose di mailcow non può cancellarla.
