using System.Globalization;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Options;

namespace MainSite.Booking;

// Il cuore: legge gli impegni dal calendario e crea eventi per una finestra
// scelta liberamente (start/end), senza slot fissi.
//
// Le regole: giorni lavorativi, finestra WorkStart-WorkEnd nel fuso indicato,
// LeadHours di preavviso, HorizonDays, e nessuna sovrapposizione con gli
// impegni già presenti (allargati di BufferMinutes).
public sealed class BookingService
{
    private readonly ICalendarGateway _calendar;
    private readonly BookingOptions _options;
    private readonly TimeOnly _workStart;
    private readonly TimeOnly _workEnd;
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
        if (_options.BufferMinutes < 0 || _options.LeadHours < 0 || _options.HorizonDays < 1
            || _options.MaxDaysPerRequest < 1)
            throw new InvalidOperationException("Booking: i limiti di tempo non possono essere negativi (buffer, lead, orizzonte, giorni).");

        _buffer = TimeSpan.FromMinutes(_options.BufferMinutes);
    }

    public bool IsConfigured => _calendar.IsConfigured;
    public string TimeZoneId => _timeZone.Id;

    // --- Impegni del calendario ---------------------------------------------

    // Intervalli occupati tra due date locali, già spezzati per giorno e
    // convertiti in HH:mm nel fuso del booking. Nessun dettaglio degli eventi.
    public async Task<IReadOnlyList<BusyBlock>> GetBusyAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!_calendar.IsConfigured)
            throw new CalendarNotConfiguredException(
                "Bookings are not configured: the calendar app password (DAV access) is missing.");

        if (to < from)
            throw new BookingValidationException("Reversed range: 'to' comes before 'from'.");

        if (to.DayNumber - from.DayNumber > _options.MaxDaysPerRequest - 1)
            to = from.AddDays(_options.MaxDaysPerRequest - 1);

        var busy = await _calendar.GetBusyAsync(
            AtLocal(from.ToDateTime(TimeOnly.MinValue)),
            AtLocal(to.AddDays(1).ToDateTime(TimeOnly.MinValue)),
            ct);

        return ToBlocks(busy, from, to);
    }

    // --- Prenotazione --------------------------------------------------------

    public async Task<BookingResponse> BookAsync(BookingRequest request, CancellationToken ct)
    {
        if (!_calendar.IsConfigured)
            throw new CalendarNotConfiguredException(
                "Bookings are not configured: the calendar app password (DAV access) is missing.");

        var name = CleanName(request.Name);
        if (name.Length is 0 or > 100)
            throw new BookingValidationException("Name is required (max 100 characters).");

        var email = (request.Email ?? "").Trim();
        if (email.Length is 0 or > 200 || !IsValidEmail(email))
            throw new BookingValidationException("The email address is not valid.");

        var note = CleanNote(request.Note);
        if (note.Length > _options.MaxNoteLength)
            throw new BookingValidationException($"The message is too long (max {_options.MaxNoteLength} characters).");

        var meetingUrl = (request.MeetingUrl ?? "").Trim();
        if (meetingUrl.Length > 500 || !IsValidMeetingUrl(meetingUrl))
            throw new BookingValidationException("A valid video call link (http or https) is required.");

        if (request.Start == default || request.End == default)
            throw new BookingValidationException("The start and end time are required.");
        if (request.End <= request.Start)
            throw new BookingValidationException("The end time must be after the start time.");
        if (request.End - request.Start < TimeSpan.FromMinutes(_options.MinDurationMinutes))
            throw new BookingValidationException(
                $"The call must last at least {_options.MinDurationMinutes} minutes.");

        var startLocal = TimeZoneInfo.ConvertTime(request.Start, _timeZone);
        var endLocal = TimeZoneInfo.ConvertTime(request.End, _timeZone);
        var date = DateOnly.FromDateTime(startLocal.DateTime);

        if (DateOnly.FromDateTime(endLocal.DateTime) != date)
            throw new BookingValidationException("The booking must start and end on the same day.");
        if (!IsWorkDay(date))
            throw new BookingValidationException("That day is not available.");

        var startTime = TimeOnly.FromDateTime(startLocal.DateTime);
        var endTime = TimeOnly.FromDateTime(endLocal.DateTime);
        if (startTime < _workStart || endTime > _workEnd)
            throw new BookingValidationException(
                $"Bookings are available between {_options.WorkStart} and {_options.WorkEnd}.");

        var now = _time.GetUtcNow();
        if (request.Start < now.AddHours(_options.LeadHours))
            throw new BookingValidationException(
                $"Bookings require at least {_options.LeadHours} hours of notice.");
        if (date > ToLocalDate(now.AddDays(_options.HorizonDays)))
            throw new BookingValidationException("That date is beyond the booking horizon.");

        var busy = await _calendar.GetBusyAsync(
            AtLocal(date.ToDateTime(TimeOnly.MinValue)),
            AtLocal(date.AddDays(1).ToDateTime(TimeOnly.MinValue)),
            ct);

        foreach (var period in busy)
        {
            if (request.Start < period.EndUtc + _buffer && period.StartUtc - _buffer < request.End)
                throw new SlotUnavailableException(
                    "That range overlaps an existing appointment: choose another time.");
        }

        var uid = $"booking-{request.Start.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{request.End.UtcDateTime:HHmmss'Z'}";
        var booking = new CalendarBooking(
            uid, request.Start, request.End, name, email,
            string.IsNullOrWhiteSpace(note) ? null : note,
            meetingUrl);

        if (!await _calendar.TryCreateEventAsync(booking, ct))
            throw new SlotUnavailableException("Someone just booked that time: choose another range.");

        return new BookingResponse(uid, request.Start, request.End);
    }

    // --- Regole e conversioni ------------------------------------------------

    private bool IsWorkDay(DateOnly date)
    {
        // ISO: 1 = lunedì ... 7 = domenica (DayOfWeek parte da domenica = 0).
        var iso = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
        return _options.WorkDays.Contains(iso);
    }

    private IReadOnlyList<BusyBlock> ToBlocks(
        IReadOnlyList<BusyPeriod> busy,
        DateOnly from,
        DateOnly to)
    {
        var blocks = new List<BusyBlock>();
        foreach (var period in busy)
        {
            var start = TimeZoneInfo.ConvertTime(period.StartUtc, _timeZone).DateTime;
            var end = TimeZoneInfo.ConvertTime(period.EndUtc, _timeZone).DateTime;
            var firstDate = DateOnly.FromDateTime(start);
            var lastDate = DateOnly.FromDateTime(end);

            for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
            {
                if (date < from || date > to)
                    continue;

                var dayStart = date.ToDateTime(TimeOnly.MinValue);
                var dayEnd = dayStart.AddDays(1);
                var blockStart = start > dayStart ? start : dayStart;
                var blockEnd = end < dayEnd ? end : dayEnd;
                if (blockEnd <= blockStart)
                    continue;

                var endLabel = blockEnd == dayEnd
                    ? "24:00"
                    : blockEnd.ToString("HH:mm", CultureInfo.InvariantCulture);

                blocks.Add(new BusyBlock(
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    blockStart.ToString("HH:mm", CultureInfo.InvariantCulture),
                    endLabel));
            }
        }

        return blocks;
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

    private static bool IsValidMeetingUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && value.Length <= 500;

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
