using System.Globalization;
using MainSite.Booking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MainSite.Pages;

// Pagina pubblica di prenotazione: mostra gli slot liberi del calendario
// CalDAV e crea un evento con il link alla video call scelto da chi prenota.
// Il rate limit "booking" resta sull'API POST; qui non va messo, altrimenti
// bloccherebbe anche le visite alla pagina.
public sealed class BookingModel(BookingService booking, IOptions<BookingOptions> options) : PageModel
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public AvailabilityResponse? Availability { get; private set; }
    public bool NotConfigured { get; private set; }
    public string? LoadError { get; private set; }
    public string? Error { get; private set; }
    public BookingResponse? Confirmed { get; private set; }
    public string? ConfirmedMeetingUrl { get; private set; }

    public string Name { get; private set; } = "";
    public string Email { get; private set; } = "";
    public string MeetingUrl { get; private set; } = "";
    public string Note { get; private set; } = "";

    public string TimeZone => Availability?.TimeZone ?? options.Value.TimeZone;
    public int SlotMinutes => Availability?.SlotMinutes ?? options.Value.SlotMinutes;
    public int MaxNoteLength => options.Value.MaxNoteLength;

    public IReadOnlyList<DayAvailability> Days =>
        Availability?.Days.Where(day => day.Slots.Count > 0).ToList() ?? [];

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

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
            Error = "Choose a time from the list.";
            await LoadAsync(ct);
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
            await LoadAsync(ct);
        }
        catch (BookingValidationException ex)
        {
            Error = ex.Message;
            await LoadAsync(ct);
        }
        catch (SlotUnavailableException ex)
        {
            Error = ex.Message;
            await LoadAsync(ct);
        }
        catch (CalendarNotConfiguredException ex)
        {
            NotConfigured = true;
            Error = ex.Message;
        }
        catch (CalendarUnavailableException ex)
        {
            Error = ex.Message;
            await LoadAsync(ct);
        }

        return Page();
    }

    public string FormatDay(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("dddd d MMMM", English)
            : date;

    public string FormatSlot(Slot slot) => $"{FormatTime(slot.Start)}–{FormatTime(slot.End)}";

    public string SlotValue(Slot slot) => slot.Start.ToString("O", CultureInfo.InvariantCulture);

    public string FormatConfirmedDay() => Confirmed is null
        ? ""
        : TimeZoneInfo.ConvertTime(Confirmed.Start, Zone).ToString("dddd d MMMM 'at' HH:mm", English);

    private TimeZoneInfo? _zone;
    private TimeZoneInfo Zone => _zone ??= TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public string FormatTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Zone).ToString("HH:mm", English);

    private async Task LoadAsync(CancellationToken ct)
    {
        NotConfigured = !booking.IsConfigured;
        if (NotConfigured)
            return;

        try
        {
            Availability = await booking.GetAvailabilityAsync(null, null, ct);
        }
        catch (CalendarNotConfiguredException)
        {
            NotConfigured = true;
        }
        catch (CalendarUnavailableException ex)
        {
            LoadError = ex.Message;
        }
        catch (BookingValidationException ex)
        {
            LoadError = ex.Message;
        }
    }
}
