# Deploy — i tre siti e Umami dietro Caddy

Uno stack docker solo, quello generato da Aspire. Caddy fa da portineria sulle
porte 80/443 e termina il TLS (certificati Let's Encrypt) di tre siti e della
dashboard Umami:

- `matteofabbri.org` (`SITE_DOMAIN`) → sito personale (progetto `site`)
- `theqult.org` (`THEQULT_DOMAIN`)   → sito `TheQult` (community di ospitalità)
- `maddalena.me` (`MADDALENA_DOMAIN`) → sito `Maddalena` (per ora un hello world)
- `analytics.matteofabbri.org` (`UMAMI_DOMAIN`) → dashboard Umami (e tracker)

Oltre a questi non ci sono altri nomi pubblici serviti da questa macchina: la
posta è su un provider esterno (MX OVH) e non passa da qui. Nel Caddyfile il
layer4 riconosce il SNI di questi nomi e inoltra il traffico al server HTTP
interno con il **PROXY protocol**, così l'IP vero del client arriva al sito
(serve a GeoIP, rate limit e `/requests`).

> mailcow non fa più parte di questo stack: la posta è su OVH. Le prenotazioni
> erano agganciate al suo CalDAV (SOGo) e oggi sono **spente**
> (`/api/booking/*` risponde 503). Il login del blog è tornato attivo con
> **account locali su SQLite** (sezione 3), senza provider esterno.

---

## 1. Stack Aspire (sito + Caddy)

Genera il compose con lo script (fa `aspire publish` + copia il contesto di
build di Caddy, che la publish da sola non porta con se'):

```powershell
cd MatteoFabbri.org
.\publish.ps1          # output in .\out (oppure: .\publish.ps1 -Output X)
```

> `publish.ps1` pubblica il sito con l'SDK e poi aggiunge un layer con **ffmpeg**
> (`deploy/site-ffmpeg.Dockerfile`): serve al blog, perché yt-dlp senza ffmpeg
> non può unire traccia video e audio e si limita a un file unico di qualità più
> bassa. La prima build scarica il pacchetto ffmpeg, le successive riusano la
> cache Docker; anche il deploy GitHub fa lo stesso passo. TheQult e Maddalena
> restano sull'immagine SDK senza ffmpeg.

Compila il file `out/.env` con i valori reali:

```ini
ACME_EMAIL=tua@mail.it
SITE_DOMAIN=matteofabbri.org             # dominio del sito (Caddy lo termina lui)
THEQULT_DOMAIN=theqult.org               # secondo sito (Caddy lo termina lui)
MADDALENA_DOMAIN=maddalena.me            # terzo sito (Caddy lo termina lui)
SITE_IMAGE=matteofabbri-site:latest       # immagine del sito (la fa publish.ps1)
SITE_PORT=8080                            # porta interna del sito
THEQULT_IMAGE=matteofabbri-theqult:latest # immagine di TheQult (la fa publish.ps1)
THEQULT_PORT=8080                         # porta interna di TheQult
MADDALENA_IMAGE=matteofabbri-maddalena:latest # immagine di Maddalena (la fa publish.ps1)
MADDALENA_PORT=8080                       # porta interna di Maddalena

# Solo primo avvio: creano il primo account locale (sezione 3).
AUTH_BOOTSTRAP_USERNAME=
AUTH_BOOTSTRAP_PASSWORD=
AUTH_BOOTSTRAP_DISPLAY_NAME=

# Umami analytics (sezione 3-bis). I tre segreti si generano una volta sola.
UMAMI_DOMAIN=analytics.matteofabbri.org
UMAMI_DB_PASSWORD=                        # openssl rand -hex 24
UMAMI_APP_SECRET=                         # openssl rand -hex 32
UMAMI_TWO_FACTOR_KEY=                     # openssl rand -hex 32 (per il 2FA)
UMAMI_WEBSITE_ID=                         # da Umami dopo il primo login; vuoto = tracker spento
```

Non ci sono variabili per posta o calendario: quelle integrazioni erano su
mailcow e oggi le prenotazioni sono spente (sezione 2). L'accesso al blog usa
account locali; le tre variabili `AUTH_BOOTSTRAP_*` servono solo al primo avvio
e vanno tolte dopo. Le variabili `UMAMI_*` configurano il servizio analytics: il
website ID si aggiunge dopo il primo login in Umami.

> `THEQULT_DOMAIN`/`MADDALENA_DOMAIN` hanno gia' un default nell'AppHost
> (`theqult.org`/`maddalena.me`): nel `.env` servono solo se vuoi cambiarli.

Avvia (la prima volta costruisce l'immagine Caddy su misura, col modulo layer4):

```bash
cd out
docker compose up -d --build
```

> **IP del client.** Il layer4 inoltra i siti al server HTTP interno con il
> **PROXY protocol** (`proxy_protocol v1` verso `127.0.0.1:8443`): senza, il
> sito vedrebbe tutte le richieste da `127.0.0.1` e GeoIP/rate limit per IP
> sarebbero inutili. Il server HTTP rilegge l'header con il listener wrapper
> `proxy_protocol` e `fallback_policy use` (con `IGNORE`, il default, l'indirizzo
> verrebbe scartato), disponibile in Caddy ≥ 2.8. Il workflow di deploy valida
> il Caddyfile con l'immagine già in uso **prima** di sostituirlo: un errore
> ferma il deploy senza toccare i siti.

---

## 1-ter. Togliere un servizio: la pulizia di docker

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
> blog**, nel volume `matteofabbri_auth-data` ci sono **gli account locali**
> (senza quel database nessun autore può più entrare in `/admin`) e nel volume
> `matteofabbri_umami-db-data` ci sono **i dati analytics**. La pulizia li
> cancella per sempre, senza chiedere conferma. Se pensi che possano ancora
> servire, salvali PRIMA di aggiornare il compose sul server:
>
> ```bash
> sudo docker run --rm -v NOME_VOLUME:/dati -v "$(pwd)":/backup alpine \
>   tar czf /backup/backup-$(date +%F).tar.gz -C /dati .
> ```

---

## 2. Prenotazioni (CalDAV — spente)

Le API vivono in **MainSite** (matteofabbri.org), sotto `/api/booking/`; la
pagina pubblica è **`/booking`**. Leggono e scrivono un calendario CalDAV. Oggi
la funzione è **spenta**: il calendario era su SOGo dentro mailcow, che non fa
più parte dello stack, e non c'è né URL né password (`Calendar:CalDavUrl` e
`Calendar:Password` sono vuoti): l'API risponde **503** e il sito parte lo
stesso. La casella è **hardcodata**: `matteo@matteofabbri.org`.

```
GET  /booking                         vista settimanale con gli impegni già occupati
GET  /api/booking/busy?from=2026-11-02&to=2026-11-08
POST /api/booking/appointments
```

La pagina **`/booking`** è una settimana tipo Outlook, larga quanto il resto del
sito: prima i campi (nome, email, **link alla video call**, nota), poi una colonna
per giorno e righe da un'ora. Gli impegni letti dal calendario sono dipinti come
blocchi **occupati** (con il buffer attorno), il resto è libero: si trascina dal
minuto di inizio a quello di fine (o si clicca per un'ora intera) e si ottiene
una finestra qualunque, arrotondata a 15 minuti. Non esistono più gli slot da
mezz'ora. Weekend, giorni passati, preavviso minimo e oltre l'orizzonte sono
chiusi; un giorno con appuntamenti ha il pallino giallo ("Partly busy"), rosso
se è pieno. Dopo una prenotazione la pagina apre la settimana dell'appuntamento,
così il blocco appena creato si vede subito. Il form chiede nome, email,
**link alla video call** e nota; la prenotazione crea l'evento con il link nella
descrizione.

Il POST accetta `start` **e** `end` (ISO 8601 con offset) più `meetingUrl` (link
http/https alla video call scelta da chi prenota), `name`, `email` e `note`.

### Riconfigurare (se un domani riaccendi le prenotazioni)

Serve un server CalDAV raggiungibile (SOGo, Nextcloud, Radicale, ...) e una
**app password** con accesso DAV per `matteo@matteofabbri.org`:

1. crea l'app password dal pannello del calendario (si vede una volta sola);
2. in sviluppo passala come variabili d'ambiente
   `Calendar__CalDavUrl` e `Calendar__Password` (oppure usa
   `Calendar__UseFake=true` per il calendario in memoria, senza credenziali);
3. in produzione mettile nell'override del compose o nel `.env` del server. Se
   una delle due è vuota le prenotazioni restano spente (503) e il sito parte
   lo stesso.

> L'URL deve finire con `/`; il percorso del calendario è
> `Calendar:CalendarPath` (default `Calendar/personal/`) e viene unito al
> principal dell'utente.

### Come si decide cosa è prenotabile

Le regole stanno in `MainSite/appsettings.json`, sezione `Booking` (si possono
sovrascrivere da ambiente con `Booking__WorkStart`, `Booking__BufferMinutes`, ...):

| Regola | Default | Nota |
|---|---|---|
| fuso | `Europe/Rome` | gli orari si vedono con l'offset reale (+01:00/+02:00) |
| giorni | lun–ven | ISO (`1` = lunedì) |
| orario | 09:00–18:00 | |
| durata minima | 15 min | la griglia si trascina a passi di 15' |
| buffer | 15 min | libero prima e dopo ogni impegno già in calendario |
| preavviso | 24 h | niente "tra un'ora" |
| orizzonte | 60 giorni | oltre, nessun giorno selezionabile |
| giorni per richiesta | max 31 | tetto di una singola lettura degli impegni |

Gli impegni si leggono dal calendario con una `REPORT calendar-query` (ricorrenze
incluse, espanse da Ical.Net); `TRANSP:TRANSPARENT` e gli eventi annullati non
contano. **Dei tuoi impegni l'API non espone mai nulla**: restituisce solo gli
intervalli occupati, senza titolo né dettagli.

La scrittura è un `PUT` del `.ics` con `If-None-Match: *`: il nome del file
deriva dall'intervallo, così due prenotazioni dello stesso orario **collidono
(409) invece di sovrascriversi**. Prima di scrivere il servizio rilegge gli
impegni del giorno e rifiuta ogni sovrapposizione, buffer compreso. Il titolo
dell'evento è `Video call with <nome>`, la descrizione contiene link, email e
messaggio del richiedente.

L'evento nasce **`STATUS:TENTATIVE`** e **`CLASS:PRIVATE`**: la prenotazione
resta provvisoria (non confermata) finché non viene accettata o rifiutata, e i
suoi dettagli sono marcati privati. `TRANSP:OPAQUE` lo rende comunque occupato,
così lo stesso intervallo non può essere prenotato due volte mentre è in attesa.

### Esempi

```bash
curl "https://matteofabbri.org/api/booking/busy?from=2026-11-02&to=2026-11-08"
```

```json
{
  "blocks": [
    { "date": "2026-11-02", "start": "10:00", "end": "11:00" },
    { "date": "2026-11-03", "start": "14:30", "end": "16:00" }
  ]
}
```

```bash
curl -X POST https://matteofabbri.org/api/booking/appointments \
  -H "Content-Type: application/json" \
  -d '{"start":"2026-11-02T09:00:00+01:00","end":"2026-11-02T10:00:00+01:00","name":"Mario Rossi","email":"mario@example.com","meetingUrl":"https://meet.google.com/abc-defg-hij","note":"Di cosa parliamo"}'
```

Risposte: `201` con l'id; `400` dati o orario non validi; `409` intervallo non
più libero; `429` troppe richieste (5 in 10 minuti per IP); `503` prenotazioni
non configurate; `502` calendario irraggiungibile o credenziali rifiutate.

### Sviluppo locale senza credenziali

`Calendar:UseFake` = `true` (o `Calendar__UseFake=true`) monta un calendario in
memoria con due impegni di esempio: si possono provare disponibilità e
prenotazione senza toccare il server. **Mai in produzione**: le prenotazioni
non finirebbero da nessuna parte.

> Non c'è ancora l'email di conferma al richiedente: l'appuntamento compare nel
> calendario, e la risposta dell'API basta al sito per mostrare la conferma.

---

## 3. Blog

Il blog vive in **MainSite**:

| Percorso | Cosa |
|---|---|
| `/` | home = indice pubblico del blog |
| `/blog` | redirect 301 a `/` (vecchio indice) |
| `/booking` | pagina pubblica di prenotazione video call |
| `/who-i-am-not` | gli omonimi di Matteo Fabbri, con i link |
| `/blog/{slug}` | un articolo |
| `/blog/tag/{tag}` | articoli con un tag |
| `/blog/feed.xml` | feed RSS |
| `/sitemap` | sitemap leggibile |
| `/sitemap.xml` | sitemap XML |
| `/404` | pagina di errore con un media casuale |
| `/info-send` | POST: salva un body grezzo nella collezione Sperone `info` |
| `/info-bot` | elenco paginato dei payload |
| `/admin/login` | accesso autori (account locali) |
| `/admin` | amministrazione: post, lede, immagini, richieste |
| `/admin/users` | gestione account locali: crea, abilita/disabilita, password |
| `/admin/ledes` | lista editabile delle frasi del jumbotron |
| `/admin/jumbotron` | upload e gestione di foto/video del jumbotron |
| `/admin/404` | upload e gestione di foto/video della pagina 404 |

Gli articoli sono file **Markdown con front matter YAML**. In produzione stanno
nel volume Docker `blog-data`, montato in `/data/blog`: un deploy ricostruisce
il container ma non tocca il volume. Non c'è nessun database da amministrare.

### Accesso autori (account locali su SQLite)

Il login non usa più un provider esterno: gli autori sono **account locali**
con username e password, salvati in un database SQLite nel volume `auth-data`
(`/data/auth/auth.db`). Le password sono hash PBKDF2-HMAC-SHA256 (210.000
iterazioni, salt casuale): il database non contiene password in chiaro.

**Primo account (una volta sola).** Con il database vuoto il container crea un
account di bootstrap leggendo queste variabili dal `.env` del server:

```ini
AUTH_BOOTSTRAP_USERNAME=matteo
AUTH_BOOTSTRAP_PASSWORD=...
AUTH_BOOTSTRAP_DISPLAY_NAME=Matteo Fabbri
```

poi ricrea il servizio (`sudo docker compose up -d --build site`). Appena esiste
un account i valori di bootstrap vengono ignorati: **toglili dal `.env`**. Se non
sono impostati il sito parte lo stesso e `/admin/login` spiega che non ci sono
account.

**Gli altri account** si gestiscono da **`/admin/users`** (pulsante **Accounts**
in `/admin`): crea account, abilita/disabilita, imposta una nuova password.
Disabilitare un account o cambiargli la password chiude subito le sue sessioni
aperte; l'ultimo account abilitato non può essere disabilitato (niente lockout).

Requisiti: username 3-64 caratteri (`a-z`, `0-9`, `.`, `_`, `-`, deve iniziare
con una lettera o una cifra), password almeno 12 caratteri. Il login è limitato
a 20 tentativi ogni 10 minuti per IP.

### Scrivere e pubblicare

Da `/admin` crei un articolo, ne scegli lo slug (se lo lasci vuoto lo ricaviamo
dal titolo) e scrivi il testo nell'editor **WYSIWYG** (Toast UI Editor, incluso
in `wwwroot/lib/toastui-editor`): titoli, elenchi, link, tabelle, blocchi di
codice e immagini. L'editor produce **Markdown** e si può passare alla modalità
Markdown dalle schede in basso. L'HTML grezzo non viene eseguito.

La **data di pubblicazione** si interpreta nel fuso `Blog:TimeZone`
(`Europe/Rome`). Un articolo con la spunta **Bozza**, o con una data futura, non
è pubblico: lo vedono solo gli autori autenticati, con un avviso in cima. Il
feed e la sitemap contengono solo gli articoli pubblicati.

Le immagini si caricano da `/admin/media`: finiscono in `media/` dentro il
volume, si servono da `/blog/media/{file}` e il testo da incollare nell'articolo
è già accanto a ogni immagine. Formati ammessi: JPG, PNG, GIF, WebP, AVIF (max
8 MB). Niente SVG.

**Video e cartella `content`.** Nell'editor, accanto a **Save**, c'è
**Download media**: legge i link video del corpo (YouTube e simili) e li scarica
con **yt-dlp** nella cartella `content/` del volume (nessun URL pubblico, niente
pubblicazione). Il binario di yt-dlp non è nell'immagine: la prima volta viene
scaricato in `tools/` dentro il volume, poi resta lì. Con ffmpeg presente
nell'immagine (il layer di `deploy/site-ffmpeg.Dockerfile`) il download prende
la qualità migliore e unisce video e audio; senza ffmpeg, come in sviluppo su
Windows, sceglie un file unico (mp4). Da `/admin/404` la sezione
**Content folder** elenca i file e con **Copy to 404** li copia nella rotazione
della pagina 404 (i file già presenti vengono saltati). Limiti e percorsi si
configurano nella sezione `YtDlp` di `appsettings.json`.

**Cestino.** Eliminare un articolo non cancella il file: lo sposta in
`_trash/` dentro il volume. Se la cancellazione è stata un errore, si recupera
da lì.

### Bot info: /info-send e /info-bot

`POST /info-send` legge **tutto il body come stringa** (fino a 1 MB) e lo salva
nella collezione Sperone `info` con un Guid generato, timestamp, IP remoto e
paese (DB-IP). È pubblico ma limitato a **60 richieste per IP ogni 10 minuti**.
La collezione usa la stessa retention delle richieste (90 giorni) e vive nella
stessa istanza Sperone (`/data/sperone`).

`/info-bot` elenca i payload ricevuti, dal più recente, **50 per pagina**;
mostra Guid, IP, paese, dimensione e body grezzo. La pagina è **pubblica**
(come `/requests`), linkata da header e sitemap.

### 404 e sitemap

La pagina **`/404`** si apre per qualsiasi URL inesistente (`UseStatusCodePagesWithReExecute`).
Pesca a caso un media caricato da **`/admin/404`** (stanno in `404/` nel volume
del blog, `/data/blog/404`, e si servono da `/404-media/{file}`) tra JPG, PNG,
GIF, WebP, AVIF, MP4, WebM, MOV e M4V; se non ce ne sono usa i file committati
in `MainSite/wwwroot/404`; se anche quella cartella è vuota mostra un messaggio.
Immagini max 15 MB, video max 25 MB.

Il contenuto della 404 viene servito con **status HTTP 200** (soft 404 voluto):
`NotFoundModel.OnGet` imposta `Response.StatusCode = StatusCodes.Status200OK`,
quindi anche gli URL inesistenti risultano 200. Non è un errore di configurazione.

La **`/sitemap`** è la versione leggibile (pagine, articoli pubblicati, tag);
`/sitemap.xml` elenca home, `/requests`, `/booking`, `/who-i-am-not`,
`/info-bot`, articoli e tag. Entrambe sono collegate dal footer, insieme al
login; la pagina **`/who-i-am-not`** (in inglese, come tutto il sito) raccoglie
gli omonimi di Matteo Fabbri con i link alle rispettive fonti.

La home del blog ha un **jumbotron** con un media a caso: le foto e i video si
caricano da **`/admin/jumbotron`** e finiscono in `jumbotron/` dentro il volume
del blog (`/data/blog`, quindi sopravvivono ai deploy). Limiti: immagini 15 MB
(JPG, PNG, GIF, WebP, AVIF), video 25 MB (MP4, WebM, MOV, M4V). Se non c'è
niente caricato, si usano le foto committate in `MainSite/wwwroot/photos`; se
neanche quelle ci sono, un gradiente di riserva. Le icone di GitHub, Instagram
e TikTok sono sovrapposte in alto a destra. Il lede sotto il titolo è scelto a
caso tra quelli gestiti in `/admin/ledes` (Sperone, collezione `ledes`); se non
ce ne sono, non viene mostrato.

### Amministrazione e lede

**`/admin`** è la pagina di amministrazione: la raggiunge qualunque autore
autenticato (non c'è allowlist). Da lì si crea un post con **New post**, si
gestiscono i **lede**, i **media del jumbotron**, i **media della
404**, le immagini e si vedono le richieste. Il pulsante di creazione non sta
nella home.

In **`/admin/ledes`** c'è la lista editabile delle frasi del jumbotron: si
aggiungono, si modificano in riga e si eliminano (massimo 300 caratteri). La
home ne sceglie una a caso a ogni apertura; la collezione Sperone `ledes` è
permanente e vive nella stessa istanza `/data/sperone`.

### Backup di articoli e account

```bash
sudo docker run --rm -v matteofabbri_blog-data:/dati -v "$(pwd)":/backup alpine \
  tar czf /backup/blog-$(date +%F).tar.gz -C /dati .
sudo docker run --rm -v matteofabbri_auth-data:/dati -v "$(pwd)":/backup alpine \
  tar czf /backup/auth-$(date +%F).tar.gz -C /dati .
```

### Sviluppo locale

In sviluppo il database è `MainSite/App_Data/auth/auth.db` (ignorato da git).
Al primo avvio valorizza `Auth__BootstrapUsername` e `Auth__BootstrapPassword`
come variabili d'ambiente, poi crea gli altri account da `/admin/users`. Per
ripartire da zero basta cancellare la cartella `MainSite/App_Data/auth`.

### Se l'accesso non funziona

- **"No accounts yet"**: il database è vuoto; imposta le credenziali di
  bootstrap e riavvia il container.
- **"The account database is unavailable"**: il volume `auth-data` non è
  scrivibile (UID 1654) o il file è corrotto; log: `sudo docker compose logs site`.
- **Password dimenticata**: un altro autore può reimpostarla da `/admin/users`.
  Se non c'è nessun altro account, cancella `auth.db` dal volume e riparti dal
  bootstrap (perdi solo gli account, non gli articoli).

---

## 3-bis. Umami (analytics)

Umami è l'analytics **cookieless** del sito: dashboard su
`https://analytics.matteofabbri.org`, tracker caricato dalle pagine pubbliche di
matteofabbri.org. Non usa cookie e non richiede un banner di consenso.

**Composizione.** Due container nello stesso stack: `umami` (immagine ufficiale
`docker.umami.is/umami-software/umami:latest`) e `umami-db`
(`postgres:15-alpine`, volume `umami-db-data`). Umami aspetta che il database
sia healthy prima di partire; i due container sono raggiungibili solo sulla rete
del compose, Caddy pubblica il solo sottodominio.

**Primo avvio.** Nel `.env` del server servono, una volta sola:

```ini
UMAMI_DOMAIN=analytics.matteofabbri.org
UMAMI_DB_PASSWORD=<openssl rand -hex 24>
UMAMI_APP_SECRET=<openssl rand -hex 32>
UMAMI_TWO_FACTOR_KEY=<openssl rand -hex 32>
```

Poi copia il nuovo `docker-compose.yaml` sul server (i due servizi sono nuovi) e:

```bash
cd /opt/matteofabbri && sudo docker compose up -d
```

La prima volta Umami applica le migrazioni: aspetta che l'healthcheck diventi
healthy (può richiedere un minuto). Il database vive nel volume
`matteofabbri_umami-db-data`.

**DNS.** Aggiungi il record A `analytics.matteofabbri.org` → IP del server;
Caddy emette il certificato da solo.

**Primo login.** Apri `https://analytics.matteofabbri.org` con `admin`/`umami`,
cambia subito la password e, se vuoi, abilita il 2FA (la chiave è già
configurata). La chiave del 2FA non va più cambiata dopo averlo abilitato.

**Collegare il sito.** In Umami: **Settings → Websites → Add website**, domain
`matteofabbri.org`; copia il **website ID** e mettilo nel `.env`:

```ini
UMAMI_WEBSITE_ID=<uuid>
```

poi `sudo docker compose up -d site`. Finché è vuoto il tracker non viene
renderizzato; con il valore, il layout di MainSite aggiunge a ogni pagina
(pubblica) `<script defer src="https://analytics.matteofabbri.org/script.js"
data-website-id="...">`. Le pagine `/admin` sono escluse: le visite
dell'autore sono rumore. Il tracker è cookieless.

**Backup.** Un dump consistente del database:

```bash
cd /opt/matteofabbri
sudo docker compose exec -T umami-db pg_dump -U umami umami > umami-$(date +%F).sql
```

**Aggiornare Umami.** L'immagine è `:latest`:

```bash
cd /opt/matteofabbri
sudo docker compose pull umami && sudo docker compose up -d umami
```

Le migrazioni girano da sole al riavvio.

---

## 4. Log delle richieste (Sperone embedded)

MainSite registra **ogni richiesta HTTP** in un database Sperone **embedded**:
la libreria gira nello stesso processo del sito e scrive in una cartella su
disco. Non c'è un server Sperone separato, nessuna connection string e nessun
login. È attivo di default; `RequestLog:Enabled=false` lo spegne.

Cosa finisce in ogni riga:

| Campo | Esempio |
|---|---|
| IP remoto (quello vero, non Caddy) e porta | `203.0.113.7`, `54321` |
| Metodo, host, percorso, query | `GET`, `matteofabbri.org`, `/blog`, `?tag=dotnet` |
| Status e durata | `200`, `42.7` ms |
| User-Agent grezzo | stringa completa (troncata a 512 caratteri), nessun parsing |
| Client ID | 16 caratteri esadecimali, hash di IP + User-Agent |
| Referer, Accept-Language | header grezzi (troncati) |
| Utente autenticato, se presente | `matteo@example.com` |
| Request id e trace id | per correlare con i log/tracce |

La query string viene salvata, ma i valori dei parametri sensibili (`code`,
`state`, `token`, `password`, `api_key`, …) sono sostituiti con `***`. Non
vengono salvati body, cookie né header `Authorization`.

**Come funziona.** Il middleware accoda la riga e prosegue: non aspetta mai il
disco. Un `BackgroundService` svuota la coda ogni secondo con un `InsertMany`
(un'unica transazione di scrittura). Se il database è lento o non disponibile le
righe restano in coda (default 8192) e i tentativi hanno un backoff; quando la
coda è piena le nuove si scartano e nei log del sito compare un avviso. La
retention predefinita è 90 giorni (`RetentionDays`; `0` = per sempre), applicata
da Sperone come TTL. Il full-text è BM25 puro: il modello semantico da ~2,3 GB è
disattivato (`SemanticModel.ModelLocation = null`), non viene scaricato né
caricato.

**Dove stanno i dati.** In produzione nel volume Docker
`matteofabbri_request-log-data`, montato in `/data/sperone` (la cartella è
`RequestLog__Path`). In sviluppo in `MainSite/App_Data/sperone` (ignorata da
git). Il workflow di deploy crea e chowna il volume a UID 1654 prima di avviare
il sito, come per `blog-data`. Se deployi a mano, fallo anche tu prima del
primo `up -d`:

```bash
sudo docker volume create matteofabbri_request-log-data
sudo docker run --rm -v matteofabbri_request-log-data:/data alpine chown -R 1654:1654 /data
```

**Configurazione.** I default sono in `MainSite/appsettings.json`, sezione
`RequestLog`:

| Chiave | Default | Cosa |
|---|---|---|
| `Enabled` | `true` | spegne tutto |
| `Path` | `App_Data/sperone` | cartella del database (in prod `/data/sperone`) |
| `DatabaseName` | `matteofabbri` | nome nei metadata |
| `Collection` | `requests` | collezione delle richieste |
| `RetentionDays` | `90` | TTL; `0` = per sempre |
| `QueueCapacity` | `8192` | righe in coda prima di scartare |
| `BatchSize` | `200` | righe per `InsertMany` |
| `FlushSeconds` | `1` | cadenza di svuotamento |
| `HashCacheLines`, `LogMemoryBits`, `LogPageBits` | `65536`, `23`, `22` | dimensione in memoria dei cassetti FASTER |
| `ExcludePaths` | `/health`, `/alive` | percorsi non registrati |

Per cambiare un valore in produzione aggiungi la variabile corrispondente nella
sezione `environment` dell'override (es. `RequestLog__RetentionDays: 30`) e
ricrea il container. Non ci sono segreti da configurare.

**Indici.** Ogni riga è indicizzata con scalar `timestamp`, `status`,
`durationMs`, `country` e `public`: si possono filtrare gli intervalli di
tempo, gli errori e il paese, ed escludere le pagine `/admin`. Non c'è un
indice full-text su `path`/`host`: nessuna pagina lo interrogava e nella
versione di Sperone fissata dal submodule un blocco di overflow perso faceva
fallire ogni `Update` della riga (flag manuali e backfill). Il payload JSON
completo (IP, User-Agent grezzo, Client ID, referer, ...) resta leggibile con
`Database.Get` o con `RequestLogStore.GetAsync`.

**Leggere i log.** La pagina pubblica `/requests` è la UI: mappa per paese e
ultime richieste. Per esplorare il database in altro modo si usa la libreria (o
si esporta la collezione con `Database.ExportCollection`). Per un backup
consistente ferma il sito, copia il volume e riavvialo:

```bash
cd /opt/matteofabbri
sudo docker compose stop site
sudo docker run --rm -v matteofabbri_request-log-data:/dati -v "$(pwd)":/backup alpine \
  tar czf /backup/request-log-$(date +%F).tar.gz -C /dati .
sudo docker compose start site
```

**Sviluppo locale.** Non serve avviare niente: `aspire run` (o `dotnet run`)
crea `MainSite/App_Data/sperone` alla prima richiesta. Per partire puliti basta
cancellare quella cartella. Il percorso si può cambiare con
`RequestLog__Path`. Su un clone nuovo serve `git submodule update --init` (o
clonare con `--recurse-submodules`): il motore Sperone sta in
`external/SPERONE`.

**Se non scrive:** guarda i log del sito (`Log richieste su Sperone embedded`).
Le cause tipiche sono la cartella non scrivibile (permessi del volume/UID),
disco pieno o database corrotto. La funzione non blocca mai il sito: in caso di
problema si perde il log, non la richiesta. Se il container non ha il volume
`request-log-data`, i log finiscono dentro il container e spariscono al deploy
successivo: controlla `docker compose config` / `docker inspect` del sito.

### Mappa delle richieste per paese

La pagina **`/requests`** è pubblica e mostra una mappa del mondo con i paesi
colorati per numero di richieste e il selettore ultimi 7/30/90 giorni. Sotto ci
sono la tabella ordinata e le richieste pubbliche **pagine da 200**, con paese,
IP, Client ID e User-Agent grezzo. È aggregata su tutto il sito: non mostra
utenti autenticati e le pagine `/admin` non compaiono nell'elenco.

Paese, IP, Client ID e User-Agent nella tabella sono **link**: ogni click
aggiunge un filtro (parametri `country`, `ip`, `clientId`, `userAgent`), i
filtri si sommano e compaiono come chip rimovibili con la **X**. Periodo e
paginazione conservano i filtri. Con filtri attivi la pagina legge le richieste
pubbliche del periodo e filtra il payload (IP, Client ID e User-Agent non hanno
un indice scalare), poi ricalcola mappa, tabella paesi e totali sul
sottoinsieme.

- Mappa: [Leaflet](https://leafletjs.com) + tile
  [OpenStreetMap](https://www.openstreetmap.org/copyright); confini dei paesi da
  [Natural Earth](https://www.naturalearthdata.com) 1:110m (pubblico dominio),
  copiati in `MainSite/wwwroot/maps/countries.geojson`.
- Geolocalizzazione: **DB-IP Lite City** (CC BY 4.0), database MMDB locale.
  Nessun IP viene inviato a servizi esterni: la lookup è in-process.
- Il codice paese (ISO 3166-1 alpha-2, es. `IT`) viene indicizzato in Sperone su
  ogni nuova richiesta; IP, User-Agent grezzo e Client ID (hash di IP + UA)
  sono salvati nel payload. Le righe registrate **prima** di questa versione
  non hanno il paese e restano tra le "non geolocalizzate": la mappa parte
  dalle nuove richieste.

**Installazione/aggiornamento GeoIP.**

- Locale: `.\deploy\update-geoip.ps1` scarica DB-IP Lite in
  `MainSite/App_Data/geoip/dbip-city-lite.mmdb` (~122 MB). Se il sito era già in
  esecuzione, riavvialo: il file viene memory-mapped all'avvio.
- Produzione: il workflow di deploy copia `deploy/update-geoip.sh` in
  `/opt/matteofabbri/update-geoip.sh`, crea `/opt/matteofabbri/geoip` e scarica
  il DB se manca. L'override lo monta read-only in `/data/geoip`.
  Aggiornamento mensile:

  ```bash
  sudo /opt/matteofabbri/update-geoip.sh /opt/matteofabbri/geoip
  cd /opt/matteofabbri && sudo docker compose restart site
  ```

- Se il file manca, il log continua a funzionare (paese sconosciuto) e la
  pagina mostra un avviso con il percorso atteso. Il percorso si cambia con
  `RequestLog__GeoIpDatabasePath`.

**Lettura.** La pagina conta per paese usando gli indici Sperone (`country` +
`timestamp`) senza leggere i payload; i totali includono anche IP privati e
righe senza paese. L'elenco delle richieste usa `timestamp` + `public`, mostra
200 righe per pagina e legge solo i payload delle righe mostrate.

---

## 5. TheQult (theqult.org)

La community di ospitalità senza abbonamenti (la "versione rifatta meglio di
Couchsurfing"): profili, richieste di soggiorno e referenze. Razor Pages +
**Sperone embedded**, come i log: la libreria gira nel processo del sito e
scrive in una cartella su disco. Nessun server database, nessun segreto da
configurare. Il codice e la storia del progetto stanno nel README di
`TheQult/`.

**Dove stanno i dati.** In produzione nel volume Docker `matteofabbri_theqult-data`,
montato in `/data/theqult` (la cartella è `TheQult__DataPath`, impostata
dall'override del compose). In sviluppo in `TheQult/App_Data/theqult` (ignorata
da git). Il workflow di deploy crea e chowna il volume a UID 1654 prima di
avviare il container, come per blog e log. Le chiavi DataProtection delle
sessioni stanno nello stesso volume (`/data/theqult/keys`): un riavvio del
container non disconnette nessuno.

Tre collezioni Sperone: `members` (profili), `stays` (richieste di soggiorno),
`references` (referenze post-soggiorno). Per un backup consistente:

```bash
cd /opt/matteofabbri
sudo docker compose stop theqult
sudo docker run --rm -v matteofabbri_theqult-data:/dati -v "$(pwd)":/backup alpine \
  tar czf /backup/theqult-$(date +%F).tar.gz -C /dati .
sudo docker compose start theqult
```

> Il compose generato dall'AppHost dichiara già volume e variabile
> (`TheQult__DataPath`); l'override li aggiunge anche sul server attuale, così
> il mount vale fin da subito senza rigenerare il compose a mano.

---

## 6. DNS (perché i certificati funzionino)

Per il TLS di Caddy servono solo i record A dei siti e della dashboard Umami,
tutti verso l'IP di questa macchina:

| Record | Nome | Valore |
|--------|------|--------|
| A | `matteofabbri.org` | IP del server |
| A | `theqult.org` | IP del server |
| A | `maddalena.me` | IP del server |
| A | `analytics.matteofabbri.org` | IP del server |

`www.` è facoltativo per i tre siti: se esiste, Caddy gli fa il certificato e lo
redirige al dominio senza www. La dashboard Umami non ha un `www.`.

La posta **non** è gestita da questa macchina: gli MX dei tre domini puntano a
OVH e non hanno niente a che vedere con il deploy del sito. I nomi `mail.*`,
`autoconfig.*` e `autodiscover.*` non esistono più; non c'è nessun PTR da
configurare qui.

---

## 7. Un sito web su un altro dominio

`matteofabbri.org`, `theqult.org`, `maddalena.me` e
`analytics.matteofabbri.org` sono già cablati in Caddy. Per i loro certificati
basta il record A → IP del server (Caddy fa il resto).

Per un **altro** dominio (è un sito, non posta, quindi tocca Caddy):
1. nell'`AppHost.cs`: aggiungi il parametro del dominio e la sua `XXX_UPSTREAM`,
   sul modello di `theqult-domain`/`THEQULT_UPSTREAM`;
2. nel `Caddyfile`: aggiungi `{$XXX_DOMAIN}` alle due righe `@site` (:443 e :80)
   e crea il blocco del sito in fondo (c'è l'esempio);
3. ricostruisci: `docker compose up -d --build`;
4. record A del dominio → IP del server. Il certificato lo fa Caddy da solo.

---

## 8. Server nuovo da zero (bootstrap automatico)

C'è uno script che fa tutto sul server Linux (Debian/Ubuntu): installa docker e
avvia lo stack del sito (i tre siti, Caddy e bot-ai).

Dal PC (PowerShell), prepara e copia tutto:

```powershell
cd MatteoFabbri.org
.\publish.ps1                                              # genera .\out (compose + tutte e tre le immagini)
docker save matteofabbri-site:latest -o out\matteofabbri-site.tar
docker save matteofabbri-theqult:latest -o out\matteofabbri-theqult.tar
docker save matteofabbri-maddalena:latest -o out\matteofabbri-maddalena.tar
# COMPILA out\.env PRIMA di copiarlo (vedi sezione 1)
scp -r out deploy root@IP_SERVER:~/
```

Sul server:

```bash
sudo bash ~/deploy/server-setup.sh
```

Lo script è rilanciabile: se un passo è già fatto lo salta. Se il `.env` è
incompleto lo script si ferma e ti dice cosa compilare.

Per gli aggiornamenti successivi (i tre siti insieme; salta quelli che non sono cambiati):

```powershell
.\publish.ps1
docker save matteofabbri-site:latest matteofabbri-theqult:latest matteofabbri-maddalena:latest | ssh root@IP_SERVER docker load
scp out\docker-compose.yaml root@IP_SERVER:/opt/matteofabbri/
ssh root@IP_SERVER 'cd /opt/matteofabbri && docker compose up -d --build --remove-orphans'
```

---

## Ordine consigliato (se fai a mano)

1. stack Aspire su
2. configura il DNS e verifica i certificati

> mailcow non fa più parte di questo stack: non c'è nessuna rete condivisa da
> creare e nessuno stack di posta da avviare o aggiornare.
