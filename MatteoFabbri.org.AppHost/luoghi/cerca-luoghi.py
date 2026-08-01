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
    "GEONAMES_URL", "https://download.geonames.org/export/dump/cities1000.zip"
)
PORTA = int(os.environ.get("PORTA", "8090"))


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
    with urllib.request.urlopen(URL, timeout=300) as r:
        grezzo = r.read()
    print(f"[luoghi] scaricati {len(grezzo)/1e6:.1f} MB, costruisco l'indice", flush=True)

    posti, nomi = [], []
    with zipfile.ZipFile(io.BytesIO(grezzo)) as z:
        txt = [n for n in z.namelist() if n.endswith(".txt")][0]
        for riga in io.TextIOWrapper(z.open(txt), encoding="utf-8"):
            c = riga.split("\t")
            if len(c) < 15:
                continue
            try:
                lat, lon, abitanti = float(c[4]), float(c[5]), int(c[14] or 0)
            except ValueError:
                continue
            id_, principale, paese = len(posti), c[1], c[8]
            posti.append((id_, principale, lat, lon, paese, abitanti))

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

    parziale = DB + ".in-arrivo"
    if os.path.exists(parziale):
        os.remove(parziale)
    con = sqlite3.connect(parziale)
    con.execute("CREATE TABLE posti (id INTEGER PRIMARY KEY, nome TEXT, "
                "lat REAL, lon REAL, paese TEXT, abitanti INTEGER)")
    con.execute("CREATE TABLE nomi (posto INTEGER, cercabile TEXT, mostrato TEXT)")
    con.executemany("INSERT INTO posti VALUES (?,?,?,?,?,?)", posti)
    con.executemany("INSERT INTO nomi VALUES (?,?,?)", nomi)
    con.execute("CREATE INDEX i_nomi ON nomi (cercabile)")
    con.commit()
    con.close()
    # Nome definitivo solo a lavoro finito: un'interruzione non lascia un
    # indice monco che sembra buono.
    os.rename(parziale, DB)
    print(f"[luoghi] indice pronto: {len(posti)} luoghi, {len(nomi)} nomi", flush=True)


# L'ordine dei risultati e' tutto. Chi cerca "roma" vuole Roma, non Roman;
# chi cerca "mila" mentre digita vuole Milano, non Mila in Algeria. Quindi:
# prima il nome IDENTICO, poi quello che COMINCIA cosi', infine quello che lo
# contiene; a parita', il centro piu' popoloso.
QUERY = """
SELECT p.nome, n.mostrato, p.lat, p.lon, p.paese, p.abitanti,
       MIN(CASE WHEN n.cercabile = :q THEN 0
                WHEN n.cercabile LIKE :inizio THEN 1
                ELSE 2 END) AS bonta
FROM nomi n JOIN posti p ON p.id = n.posto
WHERE n.cercabile LIKE :dentro
GROUP BY p.id
ORDER BY bonta, p.abitanti DESC
LIMIT :quanti
"""


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
             "paese": paese, "abitanti": ab}
            for base, mostrato, la, lo, paese, ab, _ in righe
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
