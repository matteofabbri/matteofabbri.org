# Ricerca di luoghi per nome ("madrid" -> coordinate).
#
# Il pezzo che mancava: le mappe sanno disegnare i posti e Valhalla sa portarci,
# ma nessuno dei due sa TROVARE un posto dal nome. Senza, l'app puo' cercare
# solo fra le tessere gia' scaricate — cioe' quelle vicine a dove sei.
#
# Immagine minima: Python di sistema e basta, nessuna libreria da installare
# (il servizio usa solo la libreria standard e SQLite, che e' dentro Python).
FROM python:3.13-slim

COPY cerca-luoghi.py /usr/local/bin/cerca-luoghi.py
RUN chmod +x /usr/local/bin/cerca-luoghi.py

EXPOSE 8090
ENTRYPOINT ["python3", "/usr/local/bin/cerca-luoghi.py"]
