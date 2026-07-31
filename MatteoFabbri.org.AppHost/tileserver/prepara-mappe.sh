#!/bin/bash
# Prepara la cartella dei dati delle mappe, poi avvia tileserver-gl.
#
# Gira come root (vedi Dockerfile) perche' un volume appena creato appartiene a
# root. Tutto quello che fa e' ripetibile senza danno: se i file ci sono gia',
# non tocca niente e si parte in un secondo.
set -uo pipefail

DATI=/data
PLANET="$DATI/planet.pmtiles"
PARZIALE="$PLANET.parziale"

log() { echo "[mappe] $*"; }

mkdir -p "$DATI"

# --- 1. La mappa del mondo ---------------------------------------------------
# Protomaps pubblica un build al giorno, il cui nome e' la data (20260731.pmtiles),
# e tiene online solo l'ultima settimana: un indirizzo fisso "l'ultimo" non
# esiste. Quindi: se PLANET_URL non e' stato fissato nel .env, si parte da oggi
# e si va a ritroso finche' non si trova un build che risponde.
if [ ! -f "$PLANET" ]; then
    url="${PLANET_URL:-}"

    if [ -z "$url" ]; then
        log "cerco l'ultimo build disponibile di Protomaps..."
        for i in $(seq 0 9); do
            giorno=$(date -u -d "-$i day" +%Y%m%d)
            candidato="https://build.protomaps.com/$giorno.pmtiles"
            if curl -sfI --max-time 30 "$candidato" >/dev/null 2>&1; then
                url="$candidato"
                log "trovato: build del $giorno"
                break
            fi
        done
    fi

    if [ -z "$url" ]; then
        log "ERRORE: nessun build raggiungibile (rete? Protomaps giu'?)."
        log "Aspetto e riparto: il container riprovera' da solo."
        sleep 30
        exit 1
    fi

    log "scarico la mappa del mondo: $url"
    log "sono circa 137 GB, la prima volta ci vuole parecchio."

    # Il download di 137 GB dura ore e la rete, in tutto quel tempo, cade: il
    # primo tentativo reale e' morto a 50 GB con "HTTP/2 stream not closed
    # cleanly". Quindi:
    #  --http1.1  perche' e' proprio HTTP/2 a rompersi sui trasferimenti lunghi;
    #  -C -       riprende il file parziale invece di ricominciare da zero;
    #  il ciclo   riprova da se', SENZA contare sul riavvio del container (che
    #             non arriverebbe: docker compose qui non ha restart automatico).
    # Il rinomino finale avviene solo a download completo, cosi' un'interruzione
    # non lascia mai un file monco che SEMBRA una mappa buona.
    tentativo=1
    while [ ! -f "$PLANET" ]; do
        if curl -L -C - --http1.1 --retry 5 --retry-delay 10 --retry-connrefused \
                -o "$PARZIALE" "$url"; then
            mv "$PARZIALE" "$PLANET"
            log "mappa pronta: $(du -h "$PLANET" | cut -f1)"
        else
            fatti=$(du -h "$PARZIALE" 2>/dev/null | cut -f1)
            log "download interrotto a ${fatti:-0} (tentativo $tentativo). Riprendo fra 30s."
            tentativo=$((tentativo + 1))
            sleep 30
        fi
    done
fi

# --- 2. Font e icone ---------------------------------------------------------
# I nomi di citta' e strade li disegna il browser, ma i font glieli deve servire
# questo server, gia' pre-compilati. L'immagine ne porta UNO SOLO ("Noto Sans
# Regular", verificato); lo stile di Protomaps ne usa quattro. Senza gli altri
# tre una parte dei nomi non compare affatto. Sono ~19 MB in tutto, con le icone.
if [ -z "$(ls -A "$DATI/fonts" 2>/dev/null)" ]; then
    log "scarico font e icone (~19 MB)"
    tmp=$(mktemp -d)
    if curl -sfL --retry 5 --max-time 600 -o "$tmp/assets.zip" \
            https://github.com/protomaps/basemaps-assets/archive/refs/heads/main.zip \
        && unzip -q "$tmp/assets.zip" -d "$tmp"; then
        mkdir -p "$DATI/fonts" "$DATI/sprites"
        cp -r "$tmp"/basemaps-assets-main/fonts/. "$DATI/fonts/"
        cp -r "$tmp"/basemaps-assets-main/sprites/. "$DATI/sprites/"
        log "font e icone pronti."
    else
        log "ATTENZIONE: font non scaricati. La mappa si vedra' comunque, ma"
        log "una parte delle etichette restera' invisibile. Riprovo al riavvio."
    fi
    rm -rf "$tmp"
fi

# --- 3. La configurazione ----------------------------------------------------
# Le tre cartelle devono ESISTERE tutte, anche vuote: tileserver-gl controlla
# una per una quelle dichiarate qui sotto e se ne manca una si rifiuta di
# partire ("The specified path for ... does not exist"). Vale anche per
# "styles", che resta vuota apposta: lo stile grafico lo mette il frontend, il
# server serve solo i dati.
mkdir -p "$DATI/fonts" "$DATI/sprites" "$DATI/styles"

# Riscritta a ogni avvio: e' generata, non va modificata a mano nel volume.
# Dice al server dove sono mappa, font e icone. I percorsi sono relativi perche'
# la cartella di lavoro del container e' gia' /data.
#
# serveAllFonts e' OBBLIGATORIO qui: di suo tileserver-gl serve solo i font
# dichiarati da uno stile configurato, e stili non ne abbiamo (li porta il
# frontend). Senza, ogni font risponde 400 "Error serving font" e la mappa
# esce muta. Verificato provandolo.
cat > "$DATI/config.json" <<'FINE'
{
  "options": {
    "serveAllFonts": true,
    "paths": {
      "root": "",
      "fonts": "fonts",
      "sprites": "sprites",
      "styles": "styles",
      "pmtiles": ""
    }
  },
  "data": {
    "planet": {
      "pmtiles": "planet.pmtiles"
    }
  }
}
FINE

# --- 4. Via al server --------------------------------------------------------
# I file scaricati qui sopra sono di root; l'applicazione gira come 'node'.
chown -R node:node "$DATI" 2>/dev/null || true

log "avvio tileserver-gl"
exec runuser -u node -- \
    /usr/src/app/docker-entrypoint.sh --config "$DATI/config.json" "$@"
