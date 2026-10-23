namespace MainSite.Booking;

// Connessione al calendario CalDAV di SOGo (il groupware che mailcow include):
// la casella di posta ha già un calendario, e questo è l'unico modo per
// leggerlo e scriverci. Le credenziali NON sono la password della casella ma
// un'APP PASSWORD creata da mailcow con il protocollo DAV (le password vere
// cambiano e con 2FA non funzionano).
//
// URL tipico: https://mail.maddalena.me/SOGo/dav/
// Poi il percorso è <utente>/Calendar/personal/ e le due cose vengono unite.
public sealed class CalendarOptions
{
    public const string Section = "Calendar";

    // Base DAV. Deve finire con una "/" (viene normalizzata).
    public string CalDavUrl { get; set; } = "https://mail.maddalena.me/SOGo/dav/";

    // La casella: l'indirizzo email completo fa da nome utente.
    public string Username { get; set; } = "";

    // App password con accesso DAV. Vuota = prenotazioni spente (503), il sito
    // parte lo stesso: così un .env incompleto non butta giù i siti.
    public string Password { get; set; } = "";

    // Il calendario da leggere e su cui scrivere, relativo al principal.
    // "Calendar/personal/" è quello di default di SOGo; se un domani servisse
    // un calendario dedicato ("Appuntamenti") si mette il suo percorso qui.
    public string CalendarPath { get; set; } = "Calendar/personal/";

    // Calendario finto in memoria per lo sviluppo locale (nessuna credenziale).
    // MAI in produzione: le prenotazioni non finirebbero da nessuna parte.
    public bool UseFake { get; set; }
}
