#!/usr/bin/env bash
# Aggiorna il database DB-IP Lite City usato da MainSite per la mappa delle
# richieste. Nessuna chiave/account: il file è pubblico e gratuito (CC BY 4.0).
#
# Uso: sudo ./update-geoip.sh [destinazione]
# Default: /opt/matteofabbri/geoip
#
# Dopo l'aggiornamento riavvia il sito, così il database mmap viene riaperto:
#   cd /opt/matteofabbri && sudo docker compose restart site
set -euo pipefail

dest="${1:-/opt/matteofabbri/geoip}"
mkdir -p "$dest"

tmp_gz="$(mktemp)"
tmp_db="$(mktemp)"
trap 'rm -f "$tmp_gz" "$tmp_db"' EXIT

for offset in 0 1; do
    month="$(date -u -d "-${offset} month" +%Y-%m)"
    url="https://download.db-ip.com/free/dbip-city-lite-${month}.mmdb.gz"

    if curl -fsSL --retry 3 --connect-timeout 15 "$url" -o "$tmp_gz"; then
        gzip -dc "$tmp_gz" > "$tmp_db"
        mv "$tmp_db" "$dest/dbip-city-lite.mmdb"
        chmod 644 "$dest/dbip-city-lite.mmdb"
        echo "DB-IP Lite aggiornato: $dest/dbip-city-lite.mmdb da $url"
        exit 0
    fi
done

echo "Download DB-IP Lite fallito per il mese corrente e quello precedente." >&2
exit 1
