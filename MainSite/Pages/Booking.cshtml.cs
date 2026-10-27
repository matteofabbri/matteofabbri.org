using System.Globalization;
using MainSite.Booking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MainSite.Pages;

// Pagina pubblica di prenotazione: una vista settimanale tipo Outlook, disegnata
// lato client con /api/booking/busy. Gli impegni del calendario diventano
// blocchi occupati; il resto è selezionabile trascinando (o cliccando inizio e
// fine). Nessuno slot fisso: la durata la sceglie chi prenota.
public sealed class BookingModel(BookingService booking, IOptions<BookingOptions> options) : PageModel
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public bool NotConfigured => !booking.IsConfigured;
    public string? Error { get; private set; }
    public BookingResponse? Confirmed { get; private set; }
    public string? ConfirmedMeetingUrl { get; private set; }

    public string Name { get; private set; } = "";
    public string Email { get; private set; } = "";
    public string MeetingUrl { get; private set; } = "";
    public string Note { get; private set; } = "";

    public string TimeZone => options.Value.TimeZone;
    public int MaxNoteLength => options.Value.MaxNoteLength;
    public int HorizonDays => options.Value.HorizonDays;
    public int BufferMinutes => options.Value.BufferMinutes;
    public int MinDurationMinutes => options.Value.MinDurationMinutes;
    public string WorkStart => options.Value.WorkStart;
    public string WorkEnd => options.Value.WorkEnd;
    public string WorkDays => string.Join(',', options.Value.WorkDays);

    public string Today => LocalDate(DateTimeOffset.UtcNow);
    public string HorizonDate => LocalDate(DateTimeOffset.UtcNow.AddDays(HorizonDays));
    public string WeekStart => StartOfWeek(LocalToday);
    public string MinStart => DateTimeOffset.UtcNow
        .AddHours(options.Value.LeadHours)
        .ToString("O", CultureInfo.InvariantCulture);

    public async Task<IActionResult> OnPostAsync(
        string? start,
        string? end,
        string? name,
        string? email,
        string? meetingUrl,
        string? note,
        CancellationToken ct)
    {
        Name = (name ?? "").Trim();
        Email = (email ?? "").Trim();
        MeetingUrl = (meetingUrl ?? "").Trim();
        Note = note ?? "";

        if (!DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startAt)
            || !DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var endAt))
        {
            Error = "Pick the start and end time from the calendar.";
            return Page();
        }

        try
        {
            Confirmed = await booking.BookAsync(new BookingRequest
            {
                Start = startAt,
                End = endAt,
                Name = Name,
                Email = Email,
                MeetingUrl = MeetingUrl,
                Note = Note,
            }, ct);

            ConfirmedMeetingUrl = MeetingUrl;
            Name = Email = MeetingUrl = Note = "";
        }
        catch (BookingValidationException ex)
        {
            Error = ex.Message;
        }
        catch (SlotUnavailableException ex)
        {
            Error = ex.Message;
        }
        catch (CalendarNotConfiguredException ex)
        {
            Error = ex.Message;
        }
        catch (CalendarUnavailableException ex)
        {
            Error = ex.Message;
        }

        return Page();
    }

    public string FormatConfirmedDay()
    {
        if (Confirmed is null) return "";

        var start = TimeZoneInfo.ConvertTime(Confirmed.Start, Zone);
        var end = TimeZoneInfo.ConvertTime(Confirmed.End, Zone);
        return $"{start.ToString("dddd d MMMM 'from' HH:mm", English)}–{end.ToString("HH:mm", English)}";
    }

    public string FormatTime(DateTimeOffset value)
        => TimeZoneInfo.ConvertTime(value, Zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private DateOnly LocalToday => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime);

    private string LocalDate(DateTimeOffset instant)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string StartOfWeek(DateOnly date)
    {
        var iso = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
        return date.AddDays(-(iso - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private TimeZoneInfo? _zone;
    private TimeZoneInfo Zone => _zone ??= TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}
