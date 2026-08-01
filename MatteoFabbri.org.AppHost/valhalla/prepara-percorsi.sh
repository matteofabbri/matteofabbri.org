#!/bin/bash
# Prepara il grafo di navigazione e poi avvia il servizio dei percorsi.
#
# Tutto ripetibile senza danno: se il grafo c'e' gia', parte e basta. Ogni passo
# lungo scrive un file "sentinella" a lavoro finito, cosi' un riavvio a meta'
# riprende dal punto giusto invece di ricominciare da capo.
set -uo pipefail

D=/custom_files
CONFIG=$D/valhalla.json
PBF=$D/dati-osm.pbf
PARZIALE=$PBF.in-arrivo
TAR=$D/valhalla_tiles.tar
TILEDIR=$D/valhalla_tiles

URL="${PBF_URL:-https://download.geofabrik.de/europe-latest.osm.pbf}"
THREADS="${BUILD_THREADS:-$(nproc)}"

log() { echo "[percorsi] $*"; }

mkdir -p "$D"

# --- 0. La configurazione, RIGENERATA A OGNI AVVIO ---------------------------
# Costa un istante e non tocca il grafo: cosi' un cambio di limiti si applica
# riavviando il container, senza ricostruire nulla.
#
# I TETTI DI DISTANZA sono la ragione per cui sta qui sopra. Di suo Valhalla
# ferma moto e bici a 500 km e i pedoni a 250, e su un viaggio piu' lungo
# risponde "Path distance exceeds the max distance limit" — che dall'app si
# legge solo come "percorso non calcolato". Qui viaggiamo anche 6000 km, quindi
# 20.000 km per tutti: piu' del massimo possibile in Europa (Lisbona-Mosca sono
# ~4.000), cosi' il limite non si incontra mai. Costa solo qualche secondo di
# calcolo in piu' sulle richieste davvero lunghe.
LIMITE=20000000   # 20.000 km, in metri
log "genero la configurazione (tetto di distanza: $((LIMITE / 1000)) km per ogni mezzo)"
valhalla_build_config \
    --mjolnir-tile-dir "$TILEDIR" \
    --mjolnir-tile-extract "$TAR" \
    --mjolnir-timezone "$D/timezones.sqlite" \
    --mjolnir-admin "$D/admins.sqlite" \
    --service-limits-auto-max-distance "$LIMITE" \
    --service-limits-motorcycle-max-distance "$LIMITE" \
    --service-limits-motor-scooter-max-distance "$LIMITE" \
    --service-limits-bicycle-max-distance "$LIMITE" \
    --service-limits-pedestrian-max-distance "$LIMITE" \
    --service-limits-truck-max-distance "$LIMITE" \
    --service-limits-bus-max-distance "$LIMITE" \
    --service-limits-taxi-max-distance "$LIMITE" \
    --service-limits-multimodal-max-distance "$LIMITE" \
    > "$CONFIG"

# --- 0-bis. Grafo gia' pronto? Allora si serve e basta ------------------------
if [ -f "$TAR" ] && [ -f "$D/.grafo-completo" ]; then
    log "grafo gia' pronto, avvio il servizio"
    exec valhalla_service "$CONFIG" "$THREADS"
fi

log "il grafo non c'e' (o e' incompleto): lo costruisco. Ci vogliono ore."

# --- 1. I dati OpenStreetMap -------------------------------------------------
# Il nome provvisorio e' la protezione che conta: il file prende il nome buono
# SOLO a scaricamento completo. Senza, un download interrotto lascia un moncone
# che al riavvio verrebbe scambiato per dati validi — e' gia' successo, con il
# risultato di costruire un grafo su un tredicesimo del pianeta.
if [ ! -f "$PBF" ]; then
    log "scarico i dati OSM da $URL"
    tentativo=1
    until curl -L -C - --http1.1 --retry 20 --retry-delay 15 --retry-connrefused \
            -s -o "$PARZIALE" "$URL"; do
        log "download interrotto a $(du -h "$PARZIALE" 2>/dev/null | cut -f1), tentativo $tentativo, riprendo fra 30s"
        tentativo=$((tentativo + 1))
        sleep 30
    done
    mv "$PARZIALE" "$PBF"
    log "dati scaricati: $(du -h "$PBF" | cut -f1)"
fi

# --- 2. Fusi orari e confini -------------------------------------------------
# Servono per gli orari di arrivo corretti e per le istruzioni di svolta che
# nominano il paese ("entri in Francia"). Sono anche il pezzo che faceva
# esplodere la 3.5.1: qui girano sulla 3.8.3, che ha il fix.
if [ ! -f "$D/.fusi-fatti" ]; then
    log "costruisco i fusi orari"
    valhalla_build_timezones > "$D/timezones.sqlite" && touch "$D/.fusi-fatti"
fi

if [ ! -f "$D/.confini-fatti" ]; then
    log "costruisco i confini amministrativi"
    valhalla_build_admins -c "$CONFIG" "$PBF" && touch "$D/.confini-fatti"
fi

# --- 3. Il grafo vero --------------------------------------------------------
# E' il passo lungo: ore. Se fallisce NON si prosegue, altrimenti si finirebbe
# a servire un grafo monco che risponde "nessuna strada qui" a ogni richiesta —
# esattamente come e' andata la prima volta.
if [ ! -f "$D/.tessere-fatte" ]; then
    log "costruisco il grafo con $THREADS thread (e' il passo lungo)"
    if valhalla_build_tiles -c "$CONFIG" "$PBF"; then
        touch "$D/.tessere-fatte"
        log "grafo costruito"
    else
        log "ERRORE: la costruzione del grafo e' fallita. Non avvio il servizio:"
        log "meglio niente percorsi che percorsi sbagliati. Guarda i log qui sopra."
        sleep 60
        exit 1
    fi
fi

# --- 4. Impacchettamento -----------------------------------------------------
# Il servizio legge piu' in fretta da un solo archivio che da centomila file.
# Fatto il tar, la cartella delle tessere e' ridondante e vale un centinaio di
# GB: si butta.
if [ ! -f "$TAR" ]; then
    log "impacchetto il grafo in un archivio unico"
    valhalla_build_extract -c "$CONFIG" -v
fi

if [ -f "$TAR" ] && [ -d "$TILEDIR" ]; then
    log "libero la cartella delle tessere (l'archivio basta): $(du -sh "$TILEDIR" | cut -f1)"
    rm -rf "$TILEDIR"
fi

touch "$D/.grafo-completo"
log "tutto pronto. Avvio il servizio dei percorsi."
exec valhalla_service "$CONFIG" "$THREADS"
