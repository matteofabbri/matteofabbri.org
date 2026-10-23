#!/usr/bin/env bash
# ============================================================================
# Bootstrap del server (Debian/Ubuntu, es. VPS OVH)
# Installa docker, crea la rete condivisa, tira su mailcow e lo stack del sito.
#
# COSA SI ASPETTA DI TROVARE SUL SERVER (copiati da te via scp, vedi README):
#   ~/deploy/server-setup.sh                            <- questo script
#   ~/deploy/mailcow/docker-compose.override.yml        <- aggancio rete condivisa
#   ~/out/                                              <- output di publish.ps1
#       docker-compose.yaml, .env (COMPILATO!), caddy/,
#       matteofabbri-site.tar (opzionale: l'immagine del sito via docker save)
#
# USO:  sudo bash ~/deploy/server-setup.sh [percorso-cartella-out]
#       (default: la cartella 'out' accanto a 'deploy', non $HOME: con sudo
#        $HOME diventerebbe quello di root)
#
# Rilanciabile: ogni passo controlla se e' gia' stato fatto.
# ============================================================================
set -euo pipefail
umask 0022   # richiesto da mailcow

MAILCOW_DIR="/opt/mailcow-dockerized"
STACK_DIR="/opt/matteofabbri"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Non $HOME: con "sudo bash ~/deploy/server-setup.sh" sudo cambia $HOME in
# quello di root (/root), mentre 'out' sta accanto a questo script (copiati
# insieme via scp), non nella home di chi ha lanciato sudo.
OUT_SRC="${1:-$SCRIPT_DIR/../out}"

step() { echo; echo "=== $* ==="; }

[ "$(id -u)" -eq 0 ] || { echo "Esegui da root:  sudo bash $0"; exit 1; }

# --- 1. Prerequisiti e docker ----------------------------------------------
step "1/5 Docker"
if command -v apt-get >/dev/null 2>&1; then
    apt-get update -qq
    apt-get install -y -qq curl git ca-certificates
fi
if ! command -v docker >/dev/null 2>&1; then
    # Installer ufficiale docker (engine + compose plugin)
    curl -fsSL https://get.docker.com | sh
else
    echo "docker gia' presente: $(docker --version)"
fi
systemctl enable --now docker
docker compose version >/dev/null   # verifica che il plugin compose ci sia

# --- 2. La rete condivisa "neutra" (il corridoio) ---------------------------
step "2/5 Rete condivisa matteo-shared"
if docker network inspect matteo-shared >/dev/null 2>&1; then
    echo "rete gia' presente"
else
    docker network create matteo-shared
fi

# --- 3. mailcow (stack separato, suo ciclo di vita) --------------------------
step "3/5 mailcow"
if [ ! -d "$MAILCOW_DIR" ]; then
    git clone https://github.com/mailcow/mailcow-dockerized "$MAILCOW_DIR"
fi
cd "$MAILCOW_DIR"

if [ ! -f mailcow.conf ]; then
    echo "Configurazione iniziale di mailcow (interattiva):"
    echo "  - hostname: mail.maddalena.me"
    # generate_config.sh (versioni recenti) rifiuta di partire senza questo
    # symlink gia' presente: lo richiede per essere sicuro di essere lanciato
    # dalla cartella giusta.
    ln -sf mailcow.conf .env
    ./generate_config.sh
    # Caddy tiene 80/443 sull'host: mailcow ascolta su porte interne,
    # solo su loopback. Caddy lo raggiunge via rete condivisa.
    sed -i 's/^HTTP_PORT=.*/HTTP_PORT=8080/'      mailcow.conf
    sed -i 's/^HTTP_BIND=.*/HTTP_BIND=127.0.0.1/' mailcow.conf
    sed -i 's/^HTTPS_PORT=.*/HTTPS_PORT=8443/'    mailcow.conf
    sed -i 's/^HTTPS_BIND=.*/HTTPS_BIND=127.0.0.1/' mailcow.conf
    echo
    echo "NOTA transizione: per usare anche il nome generico OVH aggiungi in"
    echo "$MAILCOW_DIR/mailcow.conf la riga:  ADDITIONAL_SAN=vpsXXXX.ovh.net"
else
    echo "mailcow.conf gia' presente: non tocco la configurazione"
fi

cp "$SCRIPT_DIR/mailcow/docker-compose.override.yml" docker-compose.override.yml
docker compose up -d
echo "mailcow avviato (il suo certificato arrivera' appena Caddy e il DNS sono attivi)"

# --- 4. Stack del sito (compose generato da Aspire) --------------------------
step "4/5 Stack sito + Caddy"
[ -d "$OUT_SRC" ] || { echo "Cartella '$OUT_SRC' non trovata: copia la 'out' di publish.ps1 e rilancia"; exit 1; }
mkdir -p "$STACK_DIR"
cp -r "$OUT_SRC/." "$STACK_DIR/"
cd "$STACK_DIR"

# Immagini dei tre siti portate come tar (docker save)? Caricale tutte.
for tarball in matteofabbri-*.tar; do
    [ -f "$tarball" ] || continue
    docker load -i "$tarball"
    rm -f "$tarball"
done

# Il .env deve essere compilato: blocco se le voci chiave sono vuote.
if grep -qE '^(ACME_EMAIL|SITE_DOMAIN|WEBMAIL_HOST|SITE_IMAGE|SITE_PORT|THEQULT_IMAGE|THEQULT_PORT|MADDALENA_IMAGE|MADDALENA_PORT|MAILCOW_TLS_UPSTREAM|MAILCOW_HTTP_UPSTREAM)=\s*$' .env; then
    echo
    echo "FERMO QUI: $STACK_DIR/.env ha valori vuoti. Compilalo e poi:"
    echo "  cd $STACK_DIR && docker compose up -d --build"
    exit 1
fi

docker compose up -d --build --remove-orphans

# Pulizia GENERICA dei componenti rimasti orfani (servizi tolti dal compose,
# es. valhalla o postgres): funziona per label di progetto, non per nome di
# servizio. --remove-orphans ha gia' buttato i container; qui si tolgono i
# volumi e le immagini del progetto che nessun container usa piu'.
# OCCHIO: un volume orfano contiene i DATI del servizio tolto (per un DB:
# tutto quello che cera dentro). Se possono ancora servire, salvali PRIMA di
# rilanciare: da qui in poi spariscono.
# La label e' di questo stack soltanto: i volumi di mailcow restano fuori.
PROGETTO="$(basename "$STACK_DIR")"
for v in $(docker volume ls --filter label=com.docker.compose.project="$PROGETTO" --format '{{.Name}}'); do
    if [ -z "$(docker ps -aq --filter volume="$v")" ]; then
        echo "volume orfano (nessun container lo usa): $v -> rimuovo"
        docker volume rm "$v" || true
    fi
done
for img in $(docker images --filter label=com.docker.compose.project="$PROGETTO" --format '{{.ID}}'); do
    if [ -z "$(docker ps -aq --filter ancestor="$img")" ]; then
        echo "immagine orfana (nessun container la usa): $img -> rimuovo"
        docker rmi "$img" || true
    fi
done

# --- 5. Riepilogo -------------------------------------------------------------
step "5/5 Stato"
docker compose ls
echo
echo "Fatto. Ricorda (vedi deploy/README.md):"
echo "  - DNS: A/MX/PTR + SPF/DKIM/DMARC dal pannello mailcow"
echo "  - admin mailcow: https://<MAILCOW_HOSTNAME>  (user: admin, pass: moohoo -> CAMBIALA)"
