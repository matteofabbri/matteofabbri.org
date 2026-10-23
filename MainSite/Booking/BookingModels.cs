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
// affare di chi calcola gli slot.
public readonly record struct BusyPeriod(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

// L'appuntamento da creare. L'identificativo è deterministico sullo slot
// (booking-20261103T090000Z): è anche il nome del file .ics sul server, quindi
// due prenotazioni dello stesso orario collidono invece di sovrapporsi.
public sealed record CalendarBooking(
    string Uid,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Name,
    string Email,
    string? Note);

// --- Richieste e risposte dell'API -----------------------------------------
public sealed class BookingRequest
{
    // Istante di inizio richiesto. Il client lo manda con offset esplicito
    // (2026-11-03T10:00:00+01:00): gli slot pubblicati sono esattamente così.
    public DateTimeOffset Start { get; init; }
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string? Note { get; init; }
}

public sealed record Slot(DateTimeOffset Start, DateTimeOffset End);

public sealed record DayAvailability(string Date, IReadOnlyList<Slot> Slots);

public sealed record AvailabilityResponse(
    string TimeZone,
    int SlotMinutes,
    IReadOnlyList<DayAvailability> Days);

public sealed record BookingResponse(
    string Id,
    DateTimeOffset Start,
    DateTimeOffset End);

// --- Errori con un significato per il client --------------------------------
// Richiesta sbagliata (400): nome, email, note, orario fuori dalle regole.
public sealed class BookingValidationException(string message) : Exception(message);

// Lo slot non è (più) libero (409): qualcuno è arrivato prima.
public sealed class SlotUnavailableException(string message) : Exception(message);

// Calendario non configurato (503): mancano URL o credenziali. Il sito
// funziona lo stesso, questa funzione è spenta.
public sealed class CalendarNotConfiguredException(string message) : Exception(message);

// Il calendario non risponde come dovrebbe (502/503): credenziali rifiutate,
// calendario inesistente, server SOGo irraggiungibile.
public sealed class CalendarUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
