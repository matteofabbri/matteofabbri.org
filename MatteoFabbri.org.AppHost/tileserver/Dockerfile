# tileserver-gl che si procura i dati DA SOLO al primo avvio.
#
# L'immagine ufficiale sa servire le mappe ma non sa dove prenderle: pretende di
# trovare i file gia' pronti nel suo volume. Qui sopra ci mettiamo un entrypoint
# che, se il volume e' vuoto, scarica la mappa del mondo di Protomaps e i font,
# e poi cede il posto all'entrypoint originale. Ai riavvii successivi trova
# tutto al suo posto e parte in un secondo. Il server si arrangia: nessun file
# da portare a mano.
FROM maptiler/tileserver-gl:v5.6.0

# L'immagine base non ha ne' curl ne' unzip (verificato: mancano entrambi).
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl unzip ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY prepara-mappe.sh /usr/local/bin/prepara-mappe.sh
RUN chmod +x /usr/local/bin/prepara-mappe.sh

# Si resta root apposta: un volume docker appena creato appartiene a root, e
# l'applicazione gira come utente 'node' (uid 999), che non ci potrebbe
# scrivere. Lo script sistema i permessi e poi torna 'node' da solo, prima di
# avviare il server vero.
ENTRYPOINT ["/usr/local/bin/prepara-mappe.sh"]
