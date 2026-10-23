using System.Globalization;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Options;

namespace MainSite.Booking;

// Il cuore: trasforma "il mio calendario + le mie regole" in slot prenotabili,
// e trasforma una richiesta in un evento sul calendario.
//
// Le regole, nell'ordine in cui si applicano:
//  1. giorni lavorativi e finestra oraria (in BookingOptions, nel fuso indicato);
//  2. griglia di slot di SlotMinutes dentro la finestra;
//  3. niente slot nei LeadHours successivi (il "tra un'ora" non si accetta);
//  4. niente slot che toccano un impegno, allargato di BufferMinutes;
//  5. niente oltre HorizonDays.
// Il fuso è quello di BookingOptions: gli slot escono con il loro offset reale
// (es. 10:00+01:00 d'inverno, 10:00+02:00 d'estate), così il frontend non deve
// indovinare niente.
public sealed class BookingService
{
    private readonly ICalendarGateway _calendar;
    private readonly BookingOptions _options;
    private readonly TimeOnly _workStart;
    private readonly TimeOnly _workEnd;
    private readonly long _slotTicks;
    private readonly TimeSpan _buffer;
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeProvider _time;

    public BookingService(ICalendarGateway calendar, IOptions<BookingOptions> options, TimeProvider time)
    {
        _calendar = calendar;
        _options = options.Value;
        _time = time;
        _timeZone = ResolveTimeZone(_options.TimeZone);

        _workStart = _options.WorkStartTime;
        _workEnd = _options.WorkEndTime;
        if (_workStart >= _workEnd)
            throw new InvalidOperationException("Booking:WorkStart deve precedere Booking:WorkEnd.");
        if (_options.SlotMinutes is < 5 or > 480)
            throw new InvalidOperationException("Booking:SlotMinutes deve stare tra 5 e 480.");
        if (_options.BufferMinutes < 0 || _options.LeadHours < 0 || _options.HorizonDays < 1
            || _options.DefaultDays < 1 || _options.MaxDaysPerRequest < 1)
            throw new InvalidOperationException("Booking: i limiti di tempo non possono essere negativi (buffer, lead, orizzonte, giorni).");

        _slotTicks = TimeSpan.FromMinutes(_options.SlotMinutes).Ticks;
        _buffer = TimeSpan.FromMinutes(_options.BufferMinutes);
    }

    public bool IsConfigured => _calendar.IsConfigured;
    public string TimeZoneId => _timeZone.Id;

    // --- Disponibilità -------------------------------------------------------

    public async Task<AvailabilityResponse> GetAvailabilityAsync(
        DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!_calendar.IsConfigured)
            throw new CalendarNotConfiguredException(
                "Prenotazioni non configurate: manca l'app password del calendario (accesso DAV).");

        var now = _time.GetUtcNow();
        var today = ToLocalDate(now);
        var lastBookableDay = ToLocalDate(now.AddDays(_options.HorizonDays));

        var startDate = from ?? today;
        if (startDate < today) startDate = today;

        var endDate = to ?? startDate.AddDays(_options.DefaultDays - 1);
        if (endDate < startDate)
            throw new BookingValidationException("Intervallo al contrario: 'to' viene prima di 'from'.");

        if (endDate.DayNumber - startDate.DayNumber > _options.MaxDaysPerRequest - 1)
            endDate = startDate.AddDays(_options.MaxDaysPerRequest - 1);
        if (endDate > lastBookableDay)
            endDate = lastBookableDay;

        var days = new List<DayAvailability>();
        if (startDate > endDate)
            return new AvailabilityResponse(_timeZone.Id, _options.SlotMinutes, days);

        var earliest = now.AddHours(_options.LeadHours);
        var busy = await _calendar.GetBusyAsync(
            AtLocal(startDate.ToDateTime(TimeOnly.MinValue)),
            AtLocal(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue)),
            ct);

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            if (!IsWorkDay(date)) continue;

            var slots = new List<Slot>();
            for (var time = _workStart; time.Ticks + _slotTicks <= _workEnd.Ticks; time = time.AddMinutes(_options.SlotMinutes))
            {
                var start = AtLocal(date.ToDateTime(time));
                var end = AtLocal(date.ToDateTime(time.AddMinutes(_options.SlotMinutes)));

                if (start < earliest) continue;
                if (TouchesBusy(start, end, busy)) continue;

                slots.Add(new Slot(start, end));
            }

            days.Add(new DayAvailability(
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                slots));
        }

        return new AvailabilityResponse(_timeZone.Id, _options.SlotMinutes, days);
    }

    // --- Prenotazione --------------------------------------------------------

    public async Task<BookingResponse> BookAsync(BookingRequest request, CancellationToken ct)
    {
        if (!_calendar.IsConfigured)
            throw new CalendarNotConfiguredException(
                "Prenotazioni non configurate: manca l'app password del calendario (accesso DAV).");

        var name = CleanName(request.Name);
        if (name.Length is 0 or > 100)
            throw new BookingValidationException("Il nome è obbligatorio (massimo 100 caratteri).");

        var email = (request.Email ?? "").Trim();
        if (email.Length is 0 or > 200 || !IsValidEmail(email))
            throw new BookingValidationException("L'indirizzo email non è valido.");

        var note = CleanNote(request.Note);
        if (note.Length > _options.MaxNoteLength)
            throw new BookingValidationException($"Il messaggio è troppo lungo (massimo {_options.MaxNoteLength} caratteri).");

        if (request.Start == default)
            throw new BookingValidationException("Manca l'orario dell'appuntamento.");

        // Lo slot richiesto deve essere uno di quelli PUBBLICATI: la stessa
        // funzione che alimenta la disponibilità decide, così le regole non
        // possono divergere fra "vedere" e "prenotare".
        var requested = TimeZoneInfo.ConvertTime(request.Start, _timeZone);
        var date = DateOnly.FromDateTime(requested.DateTime);
        var availability = await GetAvailabilityAsync(date, date, ct);

        var slot = availability.Days
            .SelectMany(day => day.Slots)
            .FirstOrDefault(candidate => candidate.Start == request.Start);

        if (slot is null)
            throw new SlotUnavailableException(
                "Quell'orario non è prenotabile: è occupato, fuori orario, troppo vicino o oltre l'orizzonte.");

        var uid = $"booking-{slot.Start.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
        var booking = new CalendarBooking(
            uid, slot.Start, slot.End, name, email,
            string.IsNullOrWhiteSpace(note) ? null : note);

        if (!await _calendar.TryCreateEventAsync(booking, ct))
            throw new SlotUnavailableException("Qualcuno ha appena prenotato quell'orario: scegline un altro.");

        return new BookingResponse(uid, slot.Start, slot.End);
    }

    // --- Regole e conversioni ------------------------------------------------

    private bool IsWorkDay(DateOnly date)
    {
        // ISO: 1 = lunedì ... 7 = domenica (DayOfWeek parte da domenica = 0).
        var iso = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
        return _options.WorkDays.Contains(iso);
    }

    private bool TouchesBusy(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<BusyPeriod> busy)
    {
        foreach (var period in busy)
        {
            if (start < period.EndUtc + _buffer && period.StartUtc - _buffer < end)
                return true;
        }
        return false;
    }

    private DateTimeOffset AtLocal(DateTime local)
        => new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _timeZone.GetUtcOffset(local));

    private DateOnly ToLocalDate(DateTimeOffset instant)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _timeZone).DateTime);

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"Booking:TimeZone '{id}' non riconosciuto.", ex);
        }
    }

    private static bool IsValidEmail(string value)
        => MailAddress.TryCreate(value, out var address)
           && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);

    private static string CleanName(string? value)
        => string.Join(' ', (value ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string CleanNote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var text = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c == '\n' || !char.IsControl(c))
                clean.Append(c);

        return clean.ToString();
    }
}
