# ffmpeg sopra l'immagine del sito.
#
# Il sito viene pubblicato con `dotnet publish /t:PublishContainer` (tag
# temporaneo :sdk) e questa build aggiunge solo il pacchetto ffmpeg: yt-dlp,
# che scarica i video del blog, senza ffmpeg non può unire traccia video e
# audio. Un'immagine base "solo locale" non funzionerebbe: l'SDK risolve la
# base dal registry, non dal daemon.
FROM matteofabbri-site:sdk

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
USER app
