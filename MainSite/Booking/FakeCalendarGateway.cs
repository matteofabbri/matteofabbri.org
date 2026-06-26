namespace MainSite.Booking;

// Calendario finto per lo sviluppo locale (Calendar:UseFake = true): due impegni
// di esempio e le prenotazioni tenute in memoria. Non contatta nessun server e
// NON va usato in produzione — le prenotazioni non finirebbero da nessuna parte.
public sealed class FakeCalendarGateway : ICalendarGateway
{
    private readonly Lock _lock = new();
    private readonly List<CalendarBooking> _bookings = [];
    private readonly List<BusyPeriod> _seeded;

    public FakeCalendarGateway(TimeProvider time)
    {
        var today = time.GetUtcNow().UtcDateTime.Date;
        _seeded =
        [
            // Domani mattina e dopodomani pomeriggio, in UTC: servono solo a far
            // vedere i blocchi occupati nella settimana.
            new BusyPeriod(
                new DateTimeOffset(today.AddDays(1).AddHours(9), TimeSpan.Zero),
                new DateTimeOffset(today.AddDays(1).AddHours(10), TimeSpan.Zero)),
            new BusyPeriod(
                new DateTimeOffset(today.AddDays(2).AddHours(14), TimeSpan.Zero),
                new DateTimeOffset(today.AddDays(2).AddHours(15), TimeSpan.Zero)),
        ];
    }

    public bool IsConfigured => true;

    public Task<IReadOnlyList<BusyPeriod>> GetBusyAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<BusyPeriod> busy = _seeded
                .Concat(_bookings.Select(b => new BusyPeriod(b.Start, b.End)))
                .Where(p => p.StartUtc < toUtc && fromUtc < p.EndUtc)
                .ToList();
            return Task.FromResult(busy);
        }
    }

    public Task<bool> TryCreateEventAsync(CalendarBooking booking, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_bookings.Any(b => b.Uid == booking.Uid))
                return Task.FromResult(false);

            _bookings.Add(booking);
            return Task.FromResult(true);
        }
    }
}
