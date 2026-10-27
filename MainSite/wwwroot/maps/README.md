# Confini delle regioni italiane

`italy-regions.geojson` contiene i confini delle 20 regioni italiane, derivati
dai dati ISTAT e distribuiti da
[openpolis/geojson-italy](https://github.com/openpolis/geojson-italy) con
licenza **CC BY 4.0** (© 2018-2021 Openpolis, dati ISTAT).

Proprietà usate dalla pagina `/admin/richieste`:

- `reg_name` — nome della regione
- `reg_istat_code_num` — codice ISTAT 1-20
- `reg_iso_3166_2` — codice ISO 3166-2 (es. `IT-62`), lo stesso usato dai
  database GeoIP

La mappa usa [Leaflet 1.9.4](https://leafletjs.com) (BSD-2-Clause, vedi
`../lib/leaflet/LICENSE`) e le tile di
[OpenStreetMap](https://www.openstreetmap.org/copyright) (© OpenStreetMap
contributors, ODbL).
