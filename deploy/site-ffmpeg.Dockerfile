# ffmpeg sopra l'immagine del sito.
#
# Il sito viene pubblicato con `dotnet publish /t:PublishContainer` (tag
# temporaneo :sdk) e questa build aggiunge solo il pacchetto ffmpeg: yt-dlp,
# che scarica i video del blog, senza ffmpeg non può unire traccia video e
# audio. Un'immagine base "solo locale" non funzionerebbe: l'SDK risolve la
# base dal registry, non dal daemon.
FROM matteofabbri-site:sdk

USER root

# ffmpeg di Ubuntu si porta dietro la pila GPU (VAAPI/VDPAU/OpenGL): libLLVM
# (137 MB) e libgallium (42 MB) servono solo all'accelerazione hardware, che in
# un container headless non esiste. yt-dlp usa ffmpeg per decodificare e per
# unire video e audio in copia, e senza quelle due librerie funziona lo stesso.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && rm -f /usr/lib/x86_64-linux-gnu/libLLVM.so.* \
             /usr/lib/x86_64-linux-gnu/libLLVM-*.so \
             /usr/lib/x86_64-linux-gnu/libgallium-*.so*

USER app
