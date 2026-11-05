# TheQult

> Riportiamo lo spirito di gratuità che c'era una volta in internet.

**TheQult** è una comunità di ospitalità: gente che ospita gente, gratis. È la
versione rifatta meglio di quello che Couchsurfing era nel 2010 — e non è più.

## Perché esiste

Una volta c'era Couchsurfing: una comunità gratuita fatta di gente che ospitava
gente gratuitamente. Non era un servizio, era un'abitudine: scrivevi a uno
sconosciuto dell'altra parte del mondo, ti prestava il divano, e per una
notte il mondo diventava un posto dove la gente si fidava della gente.

Poi dopo il Covid è arrivata la truffa. Con un paio di click ti ritrovavi a
pagare **65 euro** per un «abbonamento a vita». Che non era a vita: oggi la
stessa azienda chiede **10 euro al mese** per poter ospitare gente gratis.
La comunità l'avevano costruita i membri, gratis. È stata poi venduta
all'asta, una cifra alla volta, a chi la usava.

TheQult nasce da lì: vogliamo riportare quello spirito di gratuità che c'era
una volta in internet. Ospitare non si paga. Non con un abbonamento, non con
una verifica, non con un tier premium.

## Le regole (poche e chiare)

1. **Gratis, per sempre.** Nessun abbonamento, nessun piano premium, nessun
   «badge di verifica» a pagamento. L'unica cosa che costa è il server, e il
   server costa meno di un caffè al mese. Se un giorno TheQult non fosse più
   gratis, avrà fallito.
2. **L'ospitalità è un regalo.** Non un servizio: niente stanze, niente
   recensioni commerciali, niente «hosting as a service». Chi ospita non deve
   niente a nessuno.
3. **La fiducia si costruisce, non si compra.** Dopo un soggiorno accettato,
   ospite e ospitato possono lasciarsi una referenza l'uno dell'altro. È
   l'unico «punteggio» che esiste qui: si guadagna viaggiando e ospitando,
   non cliccando su un pulsante verde della carta di credito.
4. **Niente pubblicità, niente vendita dei dati.** Il profilo mostra quello
   che serve per ospitare: nome, città, status di ospitalità, le referenze
   che ti sei meritato. Nulla di più, e mai venduto.

## Cos'è già in piedi

- **Profili membri**: chi sei, dove vivi, se puoi ospitare (sì / forse / no),
  quante persone entrano, che lingue parli.
- **Richieste di ospitalità**: date + messaggio personale, come una volta.
  L'host accetta o rifiuta; la richiesta resta tracciata per le referenze.
- **Referenze**: si scrivono solo dopo un soggiorno **accettato**, una sola
  per direzione. Positiva, neutrale o negativa — e il testo, che è la parte
  che conta.
- **Ricerca host per città** e pagina pubblica dei profili.

Nel canestro (`TODO`): verifica dell'email, messaggistica tra membri, eventi,
mappe, traduzioni.

## Come funziona

- **ASP.NET Core** (net10.0) con Razor Pages, dentro la soluzione
  [Matteofabbri.org](https://github.com/matteofabbri/MATTEOFABBRI.ORG):
  theqult.org arriva qui dietro Caddy, come gli altri siti dello stack.
- **Persistenza su Sperone embedded**: la libreria gira in-process, niente
  server database. Tre collezioni (`members`, `stays`, `references`) in un
  volume Docker (`theqult-data`, montato in `/data/theqult`): i profili
  sopravvivono ai deploy, come devono.
- **Account**: email + password (PBKDF2 con salt per-utente). La sessione è
  un cookie; le chiavi DataProtection stanno nel volume, quindi un riavvio
  del container non butta fuori nessuno.
- **Test**: progetto `TheQult.Tests` (xunit) su store, regole e hash.

### Sviluppo locale

```bash
dotnet run --project TheQult      # http://localhost:5181
dotnet test TheQult.Tests
```

In locale i dati finiscono in `TheQult/App_Data/theqult` (niente da
configurare); in produzione l'AppHost punta la stessa sezione `TheQult` sul
volume, esattamente come fa per il blog e i log.

## La licenza del nostro tempo

TheQult non ha un modello di business e va fiero di questo. Chi ha fatto il
vecchio internet gratis non si è arricchito: ha dormito su divani sconosciuti
e ha fatto colazione con stranieri diventati amici. Questo lo si può ancora
fare — qui, senza carta di credito.

---

*TheQult vive su [theqult.org](https://theqult.org), gestito da
[Matteo Fabbri](https://matteofabbri.org).*
