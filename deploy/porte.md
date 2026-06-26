# Porte da aprire sul server (firewall / pannello OVH)

## Verso l'esterno — DA APRIRE

| Porta | Protocollo | Chi la usa | A cosa serve |
|-------|-----------|------------|--------------|
| 80    | HTTP      | Caddy      | siti + sfide Let's Encrypt (anche quelle di mailcow, inoltrate) |
| 443   | HTTPS     | Caddy      | siti + webmail (passata sigillata a mailcow) |
| 25    | SMTP      | mailcow    | posta tra server: ricevere mail dal mondo |
| 465   | SMTPS     | mailcow    | invio dai client (porta cifrata "implicita") |
| 587   | Submission| mailcow    | invio dai client (la standard moderna) |
| 143   | IMAP      | mailcow    | lettura caselle dai client (STARTTLS) |
| 993   | IMAPS     | mailcow    | lettura caselle dai client (cifrata, la piu' usata) |
| 110   | POP3      | mailcow    | lettura POP (STARTTLS) — apri solo se usi POP |
| 995   | POP3S     | mailcow    | lettura POP cifrata — apri solo se usi POP |
| 4190  | ManageSieve | mailcow  | regole/filtri lato server dai client (es. Thunderbird) |

## Solo interne — NON aprire

| Porta | Cosa c'e' dietro |
|-------|------------------|
| 8080 / 8443 | nginx di mailcow (ascolta solo su 127.0.0.1; Caddy lo raggiunge via rete docker) |
| 18888 | dashboard Aspire (telemetria) — se vuoi vederla da fuori, passa da un tunnel ssh |
| `SITE_PORT` (8080 del container sito) | il sito, raggiunto solo da Caddy |

## Note OVH

- **SMTP in uscita (25)**: sui VPS OVH l'uscita sulla porta 25 e' bloccata di
  default anti-spam, e lo sblocco e' legato alla SINGOLA macchina, non
  all'account: sul VPS nuovo va richiesto di nuovo, anche se quello vecchio era
  sbloccato da anni. Verificalo in Fase 1 (prima mail di prova in uscita),
  PRIMA di girare il DNS. Senza sblocco le mail inviate non partono; quelle
  ricevute arrivano lo stesso.
- **PTR (reverse DNS)**: dal pannello OVH, imposta il reverse dell'IP a
  `mail.matteofabbri.org` — fondamentale per non finire in spam.
- Lo script `server-setup.sh` NON tocca il firewall di proposito: se attivi
  ufw/iptables/firewall OVH, apri le porte della prima tabella.
