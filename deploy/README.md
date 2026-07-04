# Deploy — sito + mailcow dietro Caddy

Due stack docker separati sulla stessa macchina, collegati da una rete condivisa
"neutra" (`matteo-shared`). Caddy fa da portineria sulle porte 80/443 e smista
per nome:

- `matteofabbri.org` (`SITE_DOMAIN`) → sito personale (progetto `site`)
- `theqult.org` (`THEQULT_DOMAIN`)   → API `TheQult`
- `maddalena.me` (`MADDALENA_DOMAIN`) → sito MVC `Maddalena` (include `/licensing`)
- `<dominio>/mail` (tutti e tre)     → redirect a `https://<WEBMAIL_HOST>`
- **qualunque altro nome**           → mailcow (TLS **passato sigillato**: il
  certificato se lo fa mailcow)

Tutti e tre i SITI hanno il TLS terminato da Caddy (certificati Let's Encrypt).

La regola d'oro: Caddy conosce solo i SITI. I nomi della posta
(`mail.maddalena.me`, `autodiscover.`, `autoconfig.`, domini futuri) NON vanno
dichiarati in Caddy: ci passano da soli. Per quelli si lavora solo lato
mailcow e DNS (vedi sezione 4).

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
POSTGRES_PASSWORD=                        # password del Postgres di Maddalena: generala una volta, non lasciarla vuota
MAILCOW_TLS_UPSTREAM=nginx-mailcow:8443   # nginx di mailcow, HTTPS
MAILCOW_HTTP_UPSTREAM=nginx-mailcow:8080  # nginx di mailcow, HTTP

# Licensing (Maddalena, sotto /licensing): senza queste il sito parte lo
# stesso, ma "Buy" e l'emissione delle chiavi non funzionano.
STRIPE_PUBLISHABLE=pk_live_...           # dashboard Stripe > Developers > API keys
STRIPE_SECRET=sk_live_...                # dashboard Stripe > Developers > API keys
STRIPE_WEBHOOK=whsec_...                 # dashboard Stripe > Developers > Webhooks (endpoint /licensing/sperone/webhook, evento checkout.session.completed)
LICENSING_PRIVATE_KEY=                   # chiave privata che firma le licenze portabili (vedi nota sotto)
```

> `THEQULT_DOMAIN`/`MADDALENA_DOMAIN` hanno gia' un default nell'AppHost
> (`theqult.org`/`maddalena.me`): nel `.env` servono solo se vuoi cambiarli.

> `LICENSING_PRIVATE_KEY` non va persa ne' rigenerata a cuor leggero: e' la
> chiave che verifica le licenze gia' emesse. Generala una volta sola e
> tienila al sicuro (es. gestore di password), non solo nel `.env` sul server.

Avvia (la prima volta costruisce l'immagine Caddy su misura, col modulo layer4):

```bash
cd out
docker compose up -d --build
```

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
ssh root@IP_SERVER 'cd /opt/matteofabbri && docker compose up -d --build'
```

---

## Ordine consigliato (se fai a mano)

1. `docker network create matteo-shared`
2. mailcow su (prende il suo certificato via Caddy:80)
3. stack Aspire su
4. configura il DNS e verifica i certificati

## Aggiornare mailcow

```bash
cd /percorso/mailcow-dockerized && ./update.sh
```

Lo stack Aspire non si tocca: progetto diverso, rete condivisa intatta.
