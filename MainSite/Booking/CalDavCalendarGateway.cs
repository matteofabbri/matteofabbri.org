using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.Extensions.Options;

namespace MainSite.Booking;

// Il calendario vero: CalDAV su SOGo, il groupware che mailcow include.
//
// Due sole operazioni, quelle che servono a prenotare:
//  - REPORT calendar-query sulla finestra di interesse: il server manda gli
//    eventi che la intersecano (ricorrenze incluse, il server le espande lui);
//  - PUT di un .ics con If-None-Match: * : crea l'evento SOLO se quel file non
//    esiste. Il nome del file è derivato dallo slot, quindi due prenotazioni
//    dello stesso orario collidono (412) invece di sovrapporsi.
//
// SOGo vuole Basic auth con email + app password DAV, non la password della
// casella. Gli eventi "trasparenti" (TRANSP:TRANSPARENT) e quelli annullati
// non contano come impegni: il calcolo è dentro Ical.Net (GetFreeBusy).
public sealed class CalDavCalendarGateway : ICalendarGateway
{
    private readonly HttpClient _http;
    private readonly Uri? _calendarUrl;
    private readonly string? _credentials;

    public CalDavCalendarGateway(HttpClient http, IOptions<CalendarOptions> options)
    {
        _http = http;

        var settings = options.Value;
        if (!string.IsNullOrWhiteSpace(settings.CalDavUrl)
            && !string.IsNullOrWhiteSpace(settings.Username)
            && !string.IsNullOrWhiteSpace(settings.Password))
        {
            _calendarUrl = BuildCalendarUrl(settings);
            var raw = $"{settings.Username.Trim()}:{settings.Password}";
            _credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }
    }

    public bool IsConfigured => _calendarUrl is not null;

    public async Task<IReadOnlyList<BusyPeriod>> GetBusyAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var calendarUrl = RequireCalendarUrl();

        var query = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop>
                <d:getetag/>
                <c:calendar-data/>
              </d:prop>
              <c:filter>
                <c:comp-filter name="VCALENDAR">
                  <c:comp-filter name="VEVENT">
                    <c:time-range start="{fromUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}" end="{toUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}"/>
                  </c:comp-filter>
                </c:comp-filter>
              </c:filter>
            </c:calendar-query>
            """;

        using var request = new HttpRequestMessage(new HttpMethod("REPORT"), calendarUrl)
        {
            Content = new StringContent(query, Encoding.UTF8, "application/xml"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _credentials);
        request.Headers.TryAddWithoutValidation("Depth", "1");

        using var response = await SendAsync(request, "reading the calendar", ct);
        var multistatus = await response.Content.ReadAsStringAsync(ct);
        return ParseBusy(multistatus, fromUtc, toUtc);
    }

    public async Task<bool> TryCreateEventAsync(CalendarBooking booking, CancellationToken ct)
    {
        var calendarUrl = RequireCalendarUrl();
        var eventUri = new Uri(calendarUrl, Uri.EscapeDataString(booking.Uid) + ".ics");

        using var request = new HttpRequestMessage(HttpMethod.Put, eventUri)
        {
            Content = new StringContent(BuildIcs(booking), Encoding.UTF8, "text/calendar"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _credentials);
        // Il cuore dell'anti-doppia-prenotazione: se il file c'è già, il server
        // risponde 412 e non sovrascrive niente.
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");

        using var response = await SendAsync(request, "writing to the calendar", ct);

        // 412/409: lo slot è già occupato (non è un guasto).
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            return false;

        return true;
    }

    // --- Trasporto ----------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string action, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CalendarUnavailableException($"Cannot {action}: server unreachable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CalendarUnavailableException($"Cannot {action}: no response in time.", ex);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new CalendarUnavailableException(
                "The calendar rejected the credentials: check the app password with DAV access.");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new CalendarUnavailableException(
                "Calendar not found: check Calendar:CalendarPath (usually Calendar/personal/).");
        }
        if (!response.IsSuccessStatusCode && response.StatusCode is not HttpStatusCode.PreconditionFailed
            && response.StatusCode is not HttpStatusCode.Conflict)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new CalendarUnavailableException($"The calendar responded {status} during {action}.");
        }

        return response;
    }

    private static IReadOnlyList<BusyPeriod> ParseBusy(
        string multistatus, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(multistatus);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CalendarUnavailableException("The calendar response is not readable.", ex);
        }

        var busy = new List<BusyPeriod>();
        foreach (var data in document.Descendants().Where(e => e.Name.LocalName == "calendar-data"))
        {
            if (string.IsNullOrWhiteSpace(data.Value)) continue;

            Ical.Net.Calendar? calendar;
            try
            {
                calendar = Ical.Net.Calendar.Load(data.Value);
            }
            catch (Exception ex)
            {
                throw new CalendarUnavailableException("A calendar event is not readable.", ex);
            }
            if (calendar is null) continue;

            var freeBusy = calendar.GetFreeBusy(ToCalDateTime(fromUtc), ToCalDateTime(toUtc));
            if (freeBusy is null) continue;

            foreach (var entry in freeBusy.Entries)
            {
                if (entry.Status == FreeBusyStatus.Free) continue;
                if (entry.StartTime is null || entry.EffectiveEndTime is null) continue;

                var start = ToUtc(entry.StartTime);
                var end = ToUtc(entry.EffectiveEndTime);
                if (end <= start) continue;
                busy.Add(new BusyPeriod(start, end));
            }
        }

        return busy;
    }

    private static string BuildIcs(CalendarBooking booking)
    {
        var calendar = new Ical.Net.Calendar
        {
            ProductId = "-//matteofabbri.org//booking//IT",
            Version = "2.0",
        };

        var description = new StringBuilder(booking.Email);
        if (!string.IsNullOrWhiteSpace(booking.Note))
            description.Append('\n').Append(booking.Note);

        calendar.Events.Add(new CalendarEvent
        {
            Uid = booking.Uid,
            DtStamp = ToCalDateTime(DateTimeOffset.UtcNow),
            DtStart = ToCalDateTime(booking.Start),
            DtEnd = ToCalDateTime(booking.End),
            Summary = $"Appuntamento con {booking.Name}",
            Description = description.ToString(),
            Status = EventStatus.Confirmed,
            Transparency = TransparencyType.Opaque,
        });

        return new CalendarSerializer().SerializeToString(calendar)
               ?? throw new CalendarUnavailableException("Cannot prepare the event to write.");
    }

    // --- Utilità ------------------------------------------------------------

    private static Uri BuildCalendarUrl(CalendarOptions settings)
    {
        var baseUrl = settings.CalDavUrl.TrimEnd('/') + "/";
        var user = Uri.EscapeDataString(settings.Username.Trim());
        var path = settings.CalendarPath.Trim('/') + "/";
        return new Uri($"{baseUrl}{user}/{path}");
    }

    private Uri RequireCalendarUrl() => _calendarUrl
        ?? throw new CalendarNotConfiguredException(
            "Bookings are not configured: calendar URL, username and app password are required.");

    // CalDateTime con istanti UTC: niente TZID da risolvere a mano, e gli orari
    // restano corretti anche quando cambia l'ora legale.
    private static CalDateTime ToCalDateTime(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new CalDateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, "UTC");
    }

    private static DateTimeOffset ToUtc(CalDateTime value)
        => new(DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc));
}
