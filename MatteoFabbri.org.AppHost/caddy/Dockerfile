# Custom Caddy image: the official one plus the layer4 module (TCP/SNI
# routing). The front door inspects the name in the TLS handshake so it can
# forward the site traffic with the PROXY protocol.
FROM caddy:2-builder AS build
RUN xcaddy build \
    --with github.com/mholt/caddy-l4

FROM caddy:2
COPY --from=build /usr/bin/caddy /usr/bin/caddy
# Caddyfile incluso nell'immagine: il compose e' autosufficiente.
# Per modifiche alle regole: ricostruisci con `docker compose up -d --build`.
COPY Caddyfile /etc/caddy/Caddyfile
