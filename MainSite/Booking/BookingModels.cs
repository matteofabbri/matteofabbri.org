namespace MainSite.Booking;

// --- Il calendario dietro un'interfaccia ------------------------------------
// Due implementazioni: quella vera (CalDAV su SOGo) e quella finta per lo
// sviluppo locale. Il resto del servizio non sa da dove arrivino gli impegni.
public interface ICalendarGateway
{
    bool IsConfigured { get; }

    // Gli impegni tra i due istanti (UTC). Gli eventi "trasparenti" e quelli
    // annullati non sono impegni: il gateway li filtra già.
    Task<IReadOnlyList<BusyPeriod>> GetBusyAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    // Scrive l'appuntamento nel calendario. false = esisteva già qualcosa con
    // lo stesso identificativo (doppia prenotazione), non un errore tecnico.
    Task<bool> TryCreateEventAsync(CalendarBooking booking, CancellationToken ct);
}

// Un impegno in calendario. Sempre in UTC: la conversione al fuso locale è
// affare di chi disegna la settimana.
public readonly record struct BusyPeriod(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

// L'appuntamento da creare. L'identificativo è deterministico sull'intervallo
// (booking-20261103T090000Z-100000Z): è anche il nome del file .ics sul server,
// quindi due prenotazioni dello stesso orario collidono invece di sovrapporsi.
public sealed record CalendarBooking(
    string Uid,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Name,
    string Email,
    string? Note,
    string MeetingUrl);

// --- Richieste e risposte dell'API -----------------------------------------
public sealed class BookingRequest
{
    // La finestra richiesta: Start e End con offset esplicito. La prenotazione
    // dura quanto la persona sceglie, non è vincolata a slot fissi.
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string? Note { get; init; }

    // Il link alla video call scelto da chi prenota (Meet, Zoom, Teams, ...).
    public string MeetingUrl { get; init; } = "";
}

// Un intervallo occupato del calendario, già convertito nel fuso locale e
// spezzato per giorno. Il client disegna solo questi blocchi: nessun titolo,
// nessun dettaglio degli impegni.
public sealed record BusyBlock(string Date, string Start, string End);

public sealed record BookingResponse(
    string Id,
    DateTimeOffset Start,
    DateTimeOffset End);

// --- Errori con un significato per il client --------------------------------
// Richiesta sbagliata (400): nome, email, note, orario fuori dalle regole.
public sealed class BookingValidationException(string message) : Exception(message);

// L'intervallo non è (più) libero (409): qualcuno è arrivato prima.
public sealed class SlotUnavailableException(string message) : Exception(message);

// Calendario non configurato (503): mancano URL o credenziali. Il sito
// funziona lo stesso, questa funzione è spenta.
public sealed class CalendarNotConfiguredException(string message) : Exception(message);

// Il calendario non risponde come dovrebbe (502/503): credenziali rifiutate,
// calendario inesistente, server SOGo irraggiungibile.
public sealed class CalendarUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
