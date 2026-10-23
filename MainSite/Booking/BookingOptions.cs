namespace MainSite.Booking;

// Le regole con cui si decide quando si è prenotabili. Stanno in
// appsettings.json (sezione "Booking") e si possono sovrascrivere da .env con
// Booking__SlotMinutes, Booking__WorkStart, ... — ogni modifica di questi
// numeri è una scelta di vita, non un dettaglio tecnico.
public sealed class BookingOptions
{
    public const string Section = "Booking";

    // Fuso in cui ragionano orari e slot. I client ricevono gli istanti con il
    // loro offset (es. 2026-11-03T10:00:00+01:00), non come UTC grezzo.
    public string TimeZone { get; set; } = "Europe/Rome";

    // Giorni lavorativi in ISO: 1 = lunedì ... 7 = domenica.
    public int[] WorkDays { get; set; } = [1, 2, 3, 4, 5];

    // La finestra prenotabile, ora locale. Se WorkStart >= WorkEnd il servizio
    // si rifiuta di partire: è un errore di configurazione, non un caso limite.
    public string WorkStart { get; set; } = "09:00";
    public string WorkEnd { get; set; } = "18:00";

    // Durata di un appuntamento (e passo della griglia degli slot).
    public int SlotMinutes { get; set; } = 30;

    // Respire intorno a ogni impegno già in calendario: tra un evento e
    // l'appuntamento restano liberi questi minuti, da entrambe le parti.
    public int BufferMinutes { get; set; } = 15;

    // Con quanto anticipo minimo si può prenotare: 24h evita il "tra un'ora".
    public int LeadHours { get; set; } = 24;

    // Fin dove si può guardare/prenotare, da adesso.
    public int HorizonDays { get; set; } = 60;

    // Quanti giorni restituisce la disponibilità se il client non chiede un
    // intervallo. E quanti se ne possono chiedere al massimo in una volta:
    // il tetto evita che una singola richiesta scandagli l'intero orizzonte.
    public int DefaultDays { get; set; } = 14;
    public int MaxDaysPerRequest { get; set; } = 31;

    // Lunghezza massima del messaggio libero del richiedente.
    public int MaxNoteLength { get; set; } = 1000;

    public TimeOnly WorkStartTime => ParseTime(WorkStart, nameof(WorkStart));
    public TimeOnly WorkEndTime => ParseTime(WorkEnd, nameof(WorkEnd));

    private static TimeOnly ParseTime(string value, string name)
    {
        if (!TimeOnly.TryParseExact(value, "HH:mm", out var time))
            throw new InvalidOperationException($"Booking:{name} deve essere nel formato HH:mm (valore attuale: '{value}').");
        return time;
    }
}
