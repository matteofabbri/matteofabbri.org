using System.Net;
using System.Text;
using MainSite.Booking;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class CalDavCalendarGatewayTests
{
    [Fact]
    public async Task The_booking_event_is_tentative_and_private()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var gateway = new CalDavCalendarGateway(http, Options.Create(new CalendarOptions
        {
            CalDavUrl = "https://calendar.example/SOGo/dav/",
            Username = "matteo@example.test",
            Password = "app-password",
            CalendarPath = "Calendar/personal/",
        }));

        var booking = new CalendarBooking(
            "booking-20261102T080000Z-090000Z",
            new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)),
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1)),
            "Mario Rossi",
            "mario@example.com",
            "A note",
            "https://meet.example.com/abc-defg-hij");

        bool created = await gateway.TryCreateEventAsync(booking, default);

        Assert.True(created);
        Assert.Equal(HttpMethod.Put, handler.Method);

        string ics = Assert.IsType<string>(handler.Content);
        Assert.Contains("STATUS:TENTATIVE", ics);
        Assert.Contains("CLASS:PRIVATE", ics);
        Assert.Contains("TRANSP:OPAQUE", ics);
        Assert.DoesNotContain("CLASS:PUBLIC", ics);

        // The description is line-folded by the serializer; join it before
        // looking for the booker's details.
        string unfolded = ics.Replace("\r\n ", "").Replace("\r\n", "");
        Assert.Contains("mario@example.com", unfolded);
    }

    [Fact]
    public async Task A_tentative_booking_still_blocks_the_slot()
    {
        const string calendarData = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//test//EN
            BEGIN:VEVENT
            UID:tentative-booking
            DTSTAMP:20261101T000000Z
            DTSTART:20261102T090000Z
            DTEND:20261102T100000Z
            SUMMARY:Video call with someone
            STATUS:TENTATIVE
            TRANSP:OPAQUE
            CLASS:PRIVATE
            END:VEVENT
            END:VCALENDAR
            """;

        string multistatus = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/calendar/tentative-booking.ics</d:href>
                <d:propstat>
                  <d:prop>
                    <c:calendar-data>{calendarData}</c:calendar-data>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(multistatus, Encoding.UTF8, "application/xml"),
        });
        using var http = new HttpClient(handler);
        var gateway = new CalDavCalendarGateway(http, Options.Create(new CalendarOptions
        {
            CalDavUrl = "https://calendar.example/SOGo/dav/",
            Username = "matteo@example.test",
            Password = "app-password",
            CalendarPath = "Calendar/personal/",
        }));

        var busy = await gateway.GetBusyAsync(
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero),
            default);

        var period = Assert.Single(busy);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), period.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), period.EndUtc);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Content { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Content = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
