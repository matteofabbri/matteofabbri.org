# Deploy — sito + mailcow dietro Caddy

Due stack docker separati sulla stessa macchina, collegati da una rete condivisa
"neutra" (`matteo-shared`). Caddy fa da portineria sulle porte 80/443 e smista
per nome:

- `matteofabbri.org` (`SITE_DOMAIN`) → sito personale (TLS terminato da Caddy)
- `matteofabbri.org/mail`            → redirect a `https://<WEBMAIL_HOST>`
- **qualunque altro nome**           → mailcow (TLS **passato sigillato**: il
  certificato se lo fa mailcow)

La regola d'oro: Caddy conosce solo i SITI. I nomi della posta
(`mail.matteofabbri.org`, `autodiscover.`, `autoconfig.`, il nome OVH di
transizione, domini futuri) NON vanno dichiarati in Caddy: ci passano da soli.
Per quelli si lavora solo lato mailcow e DNS (vedi sezione 4).

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
MAILCOW_HOSTNAME=mail.matteofabbri.org

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
WEBMAIL_HOST=mail.matteofabbri.org       # bersaglio del redirect /mail
                                         # (in transizione: il nome OVH)
SITE_IMAGE=matteofabbri-site:latest      # immagine del sito (la fa publish.ps1)
SITE_PORT=8080                           # porta interna del sito
MAILCOW_TLS_UPSTREAM=nginx-mailcow:8443  # nginx di mailcow, HTTPS
MAILCOW_HTTP_UPSTREAM=nginx-mailcow:8080 # nginx di mailcow, HTTP
```

Avvia (la prima volta costruisce l'immagine Caddy su misura, col modulo layer4):

```bash
cd out
docker compose up -d --build
```

---

## 3. DNS (perché certificati e posta funzionino)

| Record | Nome | Valore |
|--------|------|--------|
| A | `matteofabbri.org` | IP del server |
| A | `mail.matteofabbri.org` | IP del server |
| A | `autodiscover.matteofabbri.org` | IP del server |
| A | `autoconfig.matteofabbri.org` | IP del server |
| MX | `matteofabbri.org` | `mail.matteofabbri.org` |
| PTR (reverse) | IP del server | `mail.matteofabbri.org` |
| TXT (SPF/DKIM/DMARC) | — | come indicato dal pannello mailcow |

Il reverse DNS (PTR) che punta a `mail.matteofabbri.org` è importante per non
finire in spam: si imposta dal pannello del provider della macchina.

---

## 4. Più domini e transizione da OVH

### Fase 1 — porting con il nome generico OVH

Mentre il DNS di matteofabbri.org punta ancora al vecchio server di posta, il
mailcow nuovo si raggiunge col nome OVH (es. `vpsXXXX.ovh.net`). In Caddy NON
si tocca niente; lato mailcow:

```ini
# mailcow.conf
MAILCOW_HOSTNAME=mail.matteofabbri.org   # il nome definitivo, da subito
ADDITIONAL_SAN=vpsXXXX.ovh.net           # il certificato copre anche il nome OVH
```

Configuri i client di prova contro `vpsXXXX.ovh.net`, porti gli account, provi.
Volendo, nel frattempo `WEBMAIL_HOST=vpsXXXX.ovh.net` nel `.env` fa puntare
`/mail` alla webmail di transizione.

### Fase 2 — vai in produzione

Giri il DNS (A/MX di matteofabbri.org → questo server, come da sezione 3),
metti `WEBMAIL_HOST=mail.matteofabbri.org` e riavvii Caddy
(`docker compose up -d`). Quando il nome OVH non serve più, togli
`ADDITIONAL_SAN` da mailcow.conf.

### Caselle @theqult.org (o altri domini di posta)

Solo pannello admin di mailcow ("Configurazione e dettagli > Domini > Aggiungi
dominio") + record DNS di theqult.org:

```
MX  theqult.org  ->  mail.matteofabbri.org
TXT (SPF/DKIM/DMARC) come indicato dal pannello mailcow
```

I client si connettono sempre a `mail.matteofabbri.org` (un server, molti
domini). Caddy non si tocca.

### Un sito web su un altro dominio (es. theqult.org)

Questo sì che tocca Caddy (è un sito, non posta):
1. nel `Caddyfile`: aggiungi il dominio alle due righe `@site` e crea il blocco
   del sito in fondo (c'è l'esempio commentato);
2. ricostruisci: `docker compose up -d --build`;
3. record A di theqult.org → IP del server. Il certificato lo fa Caddy da solo.

---

## 5. Server nuovo da zero (bootstrap automatico)

C'è uno script che fa tutto sul server Linux (Debian/Ubuntu): installa docker,
crea la rete condivisa, configura e avvia mailcow, avvia lo stack del sito.

Dal PC (PowerShell), prepara e copia tutto:

```powershell
cd MatteoFabbri.org
.\publish.ps1                                              # genera .\out
docker save matteofabbri-site:latest -o out\matteofabbri-site.tar
# COMPILA out\.env PRIMA di copiarlo (vedi sezione 2)
scp -r out deploy root@IP_SERVER:~/
```

Sul server:

```bash
sudo bash ~/deploy/server-setup.sh
```

Lo script è rilanciabile: se un passo è già fatto lo salta. L'unico momento
interattivo è la prima configurazione di mailcow (`generate_config.sh`), che
chiede l'hostname: rispondi `mail.matteofabbri.org`. Se il `.env` è incompleto
lo script si ferma e ti dice cosa compilare.

Per gli aggiornamenti successivi del solo sito:

```powershell
.\publish.ps1
docker save matteofabbri-site:latest | ssh root@IP_SERVER docker load
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
