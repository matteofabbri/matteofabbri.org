# Caddy "su misura": immagine ufficiale + modulo layer4 (smistamento TCP/SNI).
# Serve perche' la portineria deve guardare il nome nell'handshake TLS e
# PASSARE la posta sigillata a mailcow, senza decifrarla.
FROM caddy:2-builder AS build
RUN xcaddy build \
    --with github.com/mholt/caddy-l4

FROM caddy:2
COPY --from=build /usr/bin/caddy /usr/bin/caddy
# Caddyfile incluso nell'immagine: il compose e' autosufficiente.
# Per modifiche alle regole: ricostruisci con `docker compose up -d --build`.
COPY Caddyfile /etc/caddy/Caddyfile
