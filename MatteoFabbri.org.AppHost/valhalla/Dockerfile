# Valhalla (calcolo percorsi) su misura, sull'immagine UFFICIALE del progetto.
#
# Perche' non usiamo piu' ghcr.io/nilsnolde/docker-valhalla (che aveva i comodi
# comandi via variabili d'ambiente): quel repo e' stato ARCHIVIATO a marzo 2026
# e la sua unica immagine porta Valhalla 3.5.1, che ha un bug fatale per noi.
# Costruendo l'Europa e' morta con "double free or corruption (fasttop)" dopo
# due ore e mezza, lasciando un grafo monco (solo le strade locali, niente
# autostrade) che rispondeva "No suitable edges" a qualunque percorso.
# E' l'issue valhalla#4904, chiuso dalla PR #5005 nel dicembre 2024: un
# double-free dentro libxml2 su chiamate concorrenti a spatialite_cleanup_ex().
# L'immagine ufficiale e' alla 3.8.3 e quel fix ce l'ha.
#
# In cambio dobbiamo orchestrare noi i passi della costruzione: e' proprio
# quello che fa prepara-percorsi.sh, che tra l'altro sistema anche il difetto
# vecchio — un pbf scaricato a meta' non viene MAI scambiato per buono.
FROM ghcr.io/valhalla/valhalla:3.8.3

COPY prepara-percorsi.sh /usr/local/bin/prepara-percorsi.sh
RUN chmod +x /usr/local/bin/prepara-percorsi.sh

WORKDIR /custom_files
ENTRYPOINT ["/usr/local/bin/prepara-percorsi.sh"]
