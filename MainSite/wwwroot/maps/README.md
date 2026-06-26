# Confini dei paesi

`countries.geojson` contiene i confini dei paesi del mondo, derivati da
[Natural Earth](https://www.naturalearthdata.com) 1:110m
(`ne_110m_admin_0_countries`, pubblico dominio).

Proprietà usate dalla pagina `/richieste`:

- `ISO_A2` — codice ISO 3166-1 alpha-2 (chiave dei conteggi); per i pochi paesi
  in cui il dataset mette `-99` (Francia, Norvegia, Kosovo) si usa
  `ISO_A2_EH`
- `NAME_IT` — nome italiano mostrato nella mappa
- `NAME` — nome inglese di riserva

La mappa usa [Leaflet 1.9.4](https://leafletjs.com) (BSD-2-Clause, vedi
`../lib/leaflet/LICENSE`) e le tile di
[OpenStreetMap](https://www.openstreetmap.org/copyright) (© OpenStreetMap
contributors, ODbL).
