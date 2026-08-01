#!/usr/bin/env python3
"""Ricerca di luoghi per nome: "madrid" -> coordinate.

Perche' esiste: le mappe sanno DISEGNARE Madrid e Valhalla sa portarcisi, ma
nessuno dei due sa TROVARLA partendo dal nome. Senza questo pezzo un'app puo'
cercare solo fra le tessere che ha gia' scaricato, cioe' quelle intorno a dove
sei: ecco perche' trovava Grenoble a 267 km e non Madrid a 1400.

I dati sono GeoNames (cities1000: ogni centro abitato sopra i mille abitanti,
~170.000 voci, 10 MB) in uno SQLite. Si scarica da solo al primo avvio, come
fanno mappe e percorsi.

Solo libreria standard: nessuna dipendenza da installare ne' da aggiornare.
"""
import io
import json
import os
import sqlite3
import unicodedata
import urllib.request
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

DATI = os.environ.get("DATI_DIR", "/dati")
DB = os.path.join(DATI, "luoghi.sqlite")
URL = os.environ.get(
    "GEONAMES_URL", "https://download.geonames.org/export/dump/allCountries.zip"
)
PORTA = int(os.environ.get("PORTA", "8090"))

# QUALI LUOGHI TENERE, e quanto pesano nell'ordine dei risultati.
# L'elenco completo di GeoNames ha 12 milioni di voci e comprende ogni chiesa,
# hotel e vigneto: cercare "san" restituirebbe mille cappelle. Qui si tiene solo
# cio' che puo' essere la META di un viaggio, e il peso decide chi viene prima
# quando due posti si chiamano uguale.
#
# I centri abitati (P.*) non sono in questa tabella: per loro il peso e' il
# numero di abitanti, che e' un ordinamento naturale — Milano prima di Milano
# Marittima. Gli altri un peso "vero" non ce l'hanno, quindi glielo diamo noi:
# un passo alpino conta piu' di una cima qualsiasi, una regione piu' di un lago.
PESI = {
    "PCLI": 50_000_000,   # stato
    "ADM1": 5_000_000,    # regione
    "ADM2": 800_000,      # provincia
    "AIRP": 300_000,      # aeroporto
    "PASS": 120_000,      # PASSO DI MONTAGNA: per chi va in moto e' una meta
    "CAPE": 100_000,      # capo/promontorio (Capo Nord)
    "VLC": 90_000,        # vulcano
    "ISL": 80_000,        # isola
    "PRK": 60_000,        # parco
    "LK": 50_000,         # lago
    "BAY": 40_000,        # baia
    "PK": 30_000,         # cima
    "MT": 25_000,         # monte
    "BCH": 20_000,        # spiaggia
    "VAL": 15_000,        # valle
}


def normalizza(s):
    """'Nîmes' e 'Nimes' devono trovarsi a vicenda: chi digita in viaggio non
    mette i cappelli sulle vocali."""
    return "".join(
        c for c in unicodedata.normalize("NFD", s.lower())
        if unicodedata.category(c) != "Mn"
    ).strip()


def latino(s):
    """Scarta i nomi in alfabeti che non si digitano da qui (cirillico, arabo,
    cinese...): gonfiano l'indice e nessuno li cerchera' mai su questa tastiera."""
    return s and all(ord(c) < 0x0250 or c.isspace() for c in s)


def costruisci():
    """Scarica GeoNames e costruisce l'indice. Salta tutto se c'e' gia'."""
    if os.path.exists(DB):
        print("[luoghi] indice gia' pronto", flush=True)
        return

    print(f"[luoghi] scarico l'elenco dei luoghi da {URL}", flush=True)
    zip_tmp = os.path.join(DATI, "geonames.in-arrivo")
    with urllib.request.urlopen(URL, timeout=900) as r, open(zip_tmp, "wb") as f:
        while blocco := r.read(1 << 20):
            f.write(blocco)
    print(f"[luoghi] scaricati {os.path.getsize(zip_tmp)/1e6:.1f} MB, "
          f"costruisco l'indice (qualche minuto)", flush=True)

    parziale = DB + ".in-arrivo"
    if os.path.exists(parziale):
        os.remove(parziale)
    con = sqlite3.connect(parziale)
    con.execute("PRAGMA journal_mode=OFF")
    con.execute("PRAGMA synchronous=OFF")
    con.execute("CREATE TABLE posti (id INTEGER PRIMARY KEY, nome TEXT, "
                "lat REAL, lon REAL, paese TEXT, peso INTEGER, tipo TEXT)")
    con.execute("CREATE TABLE nomi (posto INTEGER, cercabile TEXT, mostrato TEXT)")

    # Si scrive a blocchi invece di tenere 12 milioni di righe in memoria:
    # l'elenco completo, caricato tutto insieme, si mangerebbe qualche GB.
    posti, nomi, n_posti, n_nomi = [], [], 0, 0

    def svuota():
        nonlocal posti, nomi
        if posti:
            con.executemany("INSERT INTO posti VALUES (?,?,?,?,?,?,?)", posti)
            con.executemany("INSERT INTO nomi VALUES (?,?,?)", nomi)
            posti, nomi = [], []

    with zipfile.ZipFile(zip_tmp) as z:
        # Il file dei dati e' il .txt piu' grande: nello zip completo ci sono
        # anche readme e simili.
        txt = max((i for i in z.infolist() if i.filename.endswith(".txt")),
                  key=lambda i: i.file_size).filename
        for riga in io.TextIOWrapper(z.open(txt), encoding="utf-8"):
            c = riga.split("\t")
            if len(c) < 15:
                continue
            classe, codice = c[6], c[7]
            try:
                abitanti = int(c[14] or 0)
            except ValueError:
                abitanti = 0

            # Il filtro: centri abitati sempre, il resto solo se e' nell'elenco
            # dei tipi che possono essere una meta.
            if classe == "P":
                peso = abitanti
            elif codice in PESI:
                peso = PESI[codice]
            else:
                continue

            try:
                lat, lon = float(c[4]), float(c[5])
            except ValueError:
                continue

            id_, principale, paese = n_posti, c[1], c[8]
            n_posti += 1
            posti.append((id_, principale, lat, lon, paese, peso, codice))

            # UNA RIGA PER OGNI NOME, non una stringa unica con tutto dentro.
            # E' la differenza fra trovare Milano e trovare Milanowek: cercando
            # "milano" il confronto deve avvenire su un nome intero per volta,
            # altrimenti vince chi ha "milano" all'inizio di una stringa che
            # in realta' e' la concatenazione di dieci nomi diversi.
            visti = set()
            for nome in [principale] + c[3].split(","):
                nome = nome.strip()
                if not latino(nome):
                    continue
                n = normalizza(nome)
                if n and n not in visti:
                    visti.add(n)
                    # Si conserva anche la forma con gli accenti: cosi' a chi
                    # cerca "firenze" si risponde "Firenze", non "Florence".
                    nomi.append((id_, n, nome))
                    n_nomi += 1

            if len(posti) >= 50_000:
                svuota()

    svuota()
    print(f"[luoghi] {n_posti} luoghi e {n_nomi} nomi: creo l'indice", flush=True)
    con.execute("CREATE INDEX i_nomi ON nomi (cercabile)")
    con.commit()
    con.execute("VACUUM")
    con.close()
    os.remove(zip_tmp)
    # Nome definitivo solo a lavoro finito: un'interruzione non lascia un
    # indice monco che sembra buono.
    os.rename(parziale, DB)
    print(f"[luoghi] indice pronto: {os.path.getsize(DB)/1e6:.0f} MB", flush=True)


# L'ordine dei risultati e' tutto. Chi cerca "roma" vuole Roma, non Roman;
# chi cerca "mila" mentre digita vuole Milano, non Mila in Algeria. Quindi:
# prima il nome IDENTICO, poi quello che COMINCIA cosi', infine quello che lo
# contiene; a parita', il centro piu' popoloso.
QUERY = """
SELECT p.nome, n.mostrato, p.lat, p.lon, p.paese, p.peso, p.tipo,
       MIN(CASE WHEN n.cercabile = :q THEN 0
                WHEN n.cercabile LIKE :inizio THEN 1
                ELSE 2 END) AS bonta
FROM nomi n JOIN posti p ON p.id = n.posto
WHERE n.cercabile LIKE :dentro
GROUP BY p.id
ORDER BY bonta, p.peso DESC
LIMIT :quanti
"""

# Come si chiama, in italiano, il tipo di posto: l'app puo' mostrarlo accanto
# al nome per distinguere il Passo dello Stelvio dal paese omonimo.
TIPI = {
    "PASS": "passo", "CAPE": "capo", "VLC": "vulcano", "ISL": "isola",
    "PRK": "parco", "LK": "lago", "BAY": "baia", "PK": "cima", "MT": "monte",
    "BCH": "spiaggia", "VAL": "valle", "AIRP": "aeroporto",
    "PCLI": "stato", "ADM1": "regione", "ADM2": "provincia",
}


class Servizio(BaseHTTPRequestHandler):
    def do_GET(self):
        u = urlparse(self.path)
        percorso = u.path.rstrip("/")
        if percorso in ("", "/stato"):
            n = self.con().execute("SELECT COUNT(*) FROM posti").fetchone()[0]
            return self.rispondi({"stato": "ok", "luoghi": n})
        if percorso != "/cerca":
            return self.rispondi({"errore": "usa /cerca?q=nome"}, 404)

        p = parse_qs(u.query)
        q = normalizza((p.get("q") or [""])[0])
        if len(q) < 2:
            return self.rispondi({"risultati": []})
        try:
            quanti = min(int((p.get("limite") or ["10"])[0]), 50)
        except ValueError:
            quanti = 10

        righe = self.con().execute(QUERY, {
            "q": q, "inizio": q + "%", "dentro": "%" + q + "%", "quanti": quanti
        }).fetchall()

        self.rispondi({"risultati": [
            # "nome" e' quello che combacia con cio' che hai digitato (Firenze),
            # "nome_base" quello di riferimento di GeoNames (Florence).
            {"nome": mostrato, "nome_base": base, "lat": la, "lon": lo,
             "paese": paese, "tipo": TIPI.get(tipo, "citta"), "peso": peso}
            for base, mostrato, la, lo, paese, peso, tipo, _ in righe
        ]})

    def con(self):
        if not hasattr(self, "_con"):
            self._con = sqlite3.connect(DB, check_same_thread=False)
        return self._con

    def rispondi(self, corpo, codice=200):
        dati = json.dumps(corpo, ensure_ascii=False).encode("utf-8")
        self.send_response(codice)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(dati)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Cache-Control", "public, max-age=3600")
        self.end_headers()
        self.wfile.write(dati)

    def log_message(self, *_):
        pass  # niente una riga di log per ogni tasto premuto


if __name__ == "__main__":
    os.makedirs(DATI, exist_ok=True)
    costruisci()
    print(f"[luoghi] in ascolto sulla porta {PORTA}", flush=True)
    ThreadingHTTPServer(("", PORTA), Servizio).serve_forever()
