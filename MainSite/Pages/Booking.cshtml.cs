using System.Globalization;
using MainSite.Booking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MainSite.Pages;

// Pagina pubblica di prenotazione: il calendario mensile è disegnato lato
// client (JS) usando /api/booking/availability. Verde = libero, giallo =
// parzialmente libero ("forse"), rosso = occupato/non prenotabile.
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
    public int SlotMinutes => options.Value.SlotMinutes;
    public int MaxNoteLength => options.Value.MaxNoteLength;
    public int HorizonDays => options.Value.HorizonDays;
    public string WorkStart => options.Value.WorkStart;
    public string WorkEnd => options.Value.WorkEnd;
    public string WorkDays => string.Join(',', options.Value.WorkDays);
    public string Today => TimeZoneInfo
        .ConvertTime(DateTimeOffset.UtcNow, Zone)
        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<IActionResult> OnPostAsync(
        string? start,
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

        if (!DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            Error = "Pick a time from the calendar.";
            return Page();
        }

        try
        {
            Confirmed = await booking.BookAsync(new BookingRequest
            {
                Start = parsed,
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

    public string FormatConfirmedDay() => Confirmed is null
        ? ""
        : TimeZoneInfo.ConvertTime(Confirmed.Start, Zone).ToString("dddd d MMMM 'at' HH:mm", English);

    private TimeZoneInfo? _zone;
    private TimeZoneInfo Zone => _zone ??= TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}
