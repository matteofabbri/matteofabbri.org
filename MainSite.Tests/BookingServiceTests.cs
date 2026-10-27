using MainSite.Booking;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

// Le regole di prenotazione, senza né rete né calendario vero: il gateway è
// finto e l'orologio è fermo a un istante scelto dal test.
public sealed class BookingServiceTests
{
    // Novembre 2026: dopo il cambio d'ora del 25 ottobre, Roma è +01:00.
    // (Prima del cambio è +02:00: c'è un test apposta.)
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 00, 00, TimeSpan.Zero);

    [Fact]
    public async Task Un_lunedi_pieno_produce_la_griglia_completa_di_slot()
    {
        var service = CreateService(new FakeGateway(), Now);

        var monday = await service.GetAvailabilityAsync(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2), default);

        var day = Assert.Single(monday.Days);
        Assert.Equal("2026-11-02", day.Date);
        Assert.Equal(18, day.Slots.Count);                       // 09:00 → 17:30, passi di 30'
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)), day.Slots[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 17, 30, 0, TimeSpan.FromHours(1)), day.Slots[^1].Start);
        Assert.All(day.Slots, slot => Assert.Equal(TimeSpan.FromMinutes(30), slot.End - slot.Start));
    }

    [Fact]
    public async Task Il_sabato_e_la_domenica_non_hanno_slot()
    {
        var service = CreateService(new FakeGateway(), Now);

        var weekend = await service.GetAvailabilityAsync(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 1), default);

        Assert.Empty(weekend.Days);
    }

    [Fact]
    public async Task Un_impegno_chiude_anche_i_minuti_di_buffer_intorno()
    {
        var busy = new BusyPeriod(
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1)),
            new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.FromHours(1)));
        var service = CreateService(new FakeGateway(busy), Now);

        var day = (await service.GetAvailabilityAsync(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2), default)).Days.Single();

        var clockTimes = day.Slots.Select(s => TimeOnly.FromDateTime(s.Start.DateTime)).ToList();

        // 15' di buffer: 09:30 finirebbe a 10:00 (a ridosso), 11:00 partirebbe
        // a ridosso (10:45 di buffer). Restano 09:00 e poi 11:30.
        Assert.Equal(14, clockTimes.Count);                      // 18 meno i 4 chiusi dal buffer
        Assert.Equal(new TimeOnly(9, 0), clockTimes[0]);
        Assert.Equal(new TimeOnly(11, 30), clockTimes[1]);
        Assert.DoesNotContain(clockTimes, t => t >= new TimeOnly(9, 30) && t <= new TimeOnly(11, 0));
    }

    [Fact]
    public async Task Con_24_ore_di_preavviso_oggi_non_e_prenotabile_ma_domani_si()
    {
        // Lunedì 2 novembre, 09:00 a Roma (08:00 UTC).
        var service = CreateService(new FakeGateway(), new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero));

        var result = await service.GetAvailabilityAsync(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), default);

        var today = result.Days.Single(d => d.Date == "2026-11-02");
        var tomorrow = result.Days.Single(d => d.Date == "2026-11-03");
        Assert.Empty(today.Slots);
        Assert.NotEmpty(tomorrow.Slots);
    }

    [Fact]
    public async Task Guardo_oltre_l_orizzonte_e_non_ottengo_niente()
    {
        var service = CreateService(new FakeGateway(), Now);

        var result = await service.GetAvailabilityAsync(new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 5), default);

        Assert.Empty(result.Days);
    }

    [Fact]
    public async Task Il_fuso_segue_l_ora_legale()
    {
        // Due orologi: prima del cambio d'ora (25 ottobre 2026) e dopo.
        var beforeDst = CreateService(new FakeGateway(), new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var afterDst = CreateService(new FakeGateway(), new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));

        var summer = await beforeDst.GetAvailabilityAsync(new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 23), default);
        var winter = await afterDst.GetAvailabilityAsync(new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 30), default);

        Assert.Equal(TimeSpan.FromHours(2), summer.Days.Single().Slots[0].Start.Offset);
        Assert.Equal(TimeSpan.FromHours(1), winter.Days.Single().Slots[0].Start.Offset);
    }

    [Fact]
    public async Task Prenotare_uno_slot_libero_crea_l_evento_con_un_identificativo_stabile()
    {
        var gateway = new FakeGateway();
        var service = CreateService(gateway, Now);

        var response = await service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)),
            Name = "  Mario   Rossi ",
            Email = "mario@example.com",
            Note = "Vorrei parlare di X",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default);

        var created = Assert.Single(gateway.Created);
        Assert.Equal("booking-20261102T080000Z", created.Uid);
        Assert.Equal("Mario Rossi", created.Name);
        Assert.Equal("https://meet.google.com/abc-defg-hij", created.MeetingUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), created.Start);
        Assert.Equal(response.Id, created.Uid);
    }

    [Fact]
    public async Task Uno_slot_non_pubblicato_viene_rifiutato_anche_se_e_libero()
    {
        // 09:15 non è sulla griglia: la disponibilità non lo offre, e nemmeno
        // la prenotazione lo accetta.
        var service = CreateService(new FakeGateway(), Now);

        await Assert.ThrowsAsync<SlotUnavailableException>(() => service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 9, 15, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default));
    }

    [Fact]
    public async Task Se_il_calendario_dice_che_lo_slot_e_occupato_la_risposta_e_un_conflitto()
    {
        var gateway = new FakeGateway { AcceptWrites = false };
        var service = CreateService(gateway, Now);

        await Assert.ThrowsAsync<SlotUnavailableException>(() => service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("non-una-email")]
    [InlineData("mario@")]
    public async Task Un_email_non_valida_ferma_la_prenotazione(string email)
    {
        var service = CreateService(new FakeGateway(), Now);

        await Assert.ThrowsAsync<BookingValidationException>(() => service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = email,
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("non-un-link")]
    [InlineData("ftp://example.com/meeting")]
    public async Task Un_link_video_non_valido_ferma_la_prenotazione(string meetingUrl)
    {
        var service = CreateService(new FakeGateway(), Now);

        await Assert.ThrowsAsync<BookingValidationException>(() => service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = meetingUrl,
        }, default));
    }

    [Fact]
    public async Task Un_calendario_non_configurato_spegne_l_api_senza_errori_tecnici()
    {
        var service = CreateService(new FakeGateway { IsConfigured = false }, Now);

        await Assert.ThrowsAsync<CalendarNotConfiguredException>(
            () => service.GetAvailabilityAsync(null, null, default));
    }

    // --- Impalcatura ---------------------------------------------------------

    private static BookingService CreateService(ICalendarGateway gateway, DateTimeOffset now)
        => new(gateway, Options.Create(new BookingOptions()), new FixedTimeProvider(now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeGateway(params BusyPeriod[] busy) : ICalendarGateway
    {
        public List<CalendarBooking> Created { get; } = [];
        public bool IsConfigured { get; init; } = true;
        public bool AcceptWrites { get; init; } = true;

        public Task<IReadOnlyList<BusyPeriod>> GetBusyAsync(
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BusyPeriod>>(busy);

        public Task<bool> TryCreateEventAsync(CalendarBooking booking, CancellationToken ct)
        {
            if (!AcceptWrites) return Task.FromResult(false);
            Created.Add(booking);
            return Task.FromResult(true);
        }
    }
}
