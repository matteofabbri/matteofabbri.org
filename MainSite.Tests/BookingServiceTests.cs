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
    public async Task Gli_impegni_del_calendario_diventano_blocchi_in_ora_locale()
    {
        var busy = new BusyPeriod(
            new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        var service = CreateService(new FakeGateway(busy), Now);

        var blocks = await service.GetBusyAsync(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), default);

        var block = Assert.Single(blocks);
        Assert.Equal("2026-11-02", block.Date);
        Assert.Equal("10:00", block.Start);
        Assert.Equal("11:00", block.End);
    }

    [Fact]
    public async Task Un_impegno_che_scavalca_la_mezzanotte_viene_spezzato_per_giorno()
    {
        // 22:30–23:30 UTC = 23:30–00:30 a Roma (ora solare).
        var busy = new BusyPeriod(
            new DateTimeOffset(2026, 11, 2, 22, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 23, 30, 0, TimeSpan.Zero));
        var service = CreateService(new FakeGateway(busy), Now);

        var blocks = await service.GetBusyAsync(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), default);

        Assert.Collection(blocks,
            first =>
            {
                Assert.Equal("2026-11-02", first.Date);
                Assert.Equal("23:30", first.Start);
                Assert.Equal("24:00", first.End);
            },
            second =>
            {
                Assert.Equal("2026-11-03", second.Date);
                Assert.Equal("00:00", second.Start);
                Assert.Equal("00:30", second.End);
            });
    }

    [Fact]
    public async Task La_lettura_degli_impegni_non_supera_il_tetto_di_giorni()
    {
        var gateway = new FakeGateway();
        var service = CreateService(gateway, Now);

        await service.GetBusyAsync(new DateOnly(2026, 11, 2), new DateOnly(2027, 3, 1), default);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero), gateway.LastFromUtc);
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 23, 0, 0, TimeSpan.Zero), gateway.LastToUtc);
    }

    [Fact]
    public async Task Un_intervallo_libero_viene_prenotato_cosi_come_lo_si_e_scelto()
    {
        var gateway = new FakeGateway();
        var service = CreateService(gateway, Now);

        var response = await service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.FromHours(1)),
            Name = "  Mario   Rossi ",
            Email = "mario@example.com",
            Note = "Vorrei parlare di X",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default);

        var created = Assert.Single(gateway.Created);
        Assert.Equal("booking-20261102T080000Z-093000Z", created.Uid);
        Assert.Equal("Mario Rossi", created.Name);
        Assert.Equal("https://meet.google.com/abc-defg-hij", created.MeetingUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), created.Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero), created.End);
        Assert.Equal(response.Id, created.Uid);
    }

    [Fact]
    public async Task Un_orario_fuori_dalla_vecchia_griglia_di_mezz_ora_va_bene()
    {
        // 09:15–09:45 non era mai uno slot pubblicato: adesso è una finestra
        // qualunque scelta dalla persona.
        var gateway = new FakeGateway();
        var service = CreateService(gateway, Now);

        await service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 9, 15, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 11, 2, 9, 45, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default);

        Assert.Single(gateway.Created);
    }

    [Fact]
    public async Task Un_intervallo_a_ridosso_di_un_impegno_viene_rifiutato()
    {
        // Impegno 10:00–11:00 locali; 09:45–10:15 entra nel buffer di 15'.
        var busy = new BusyPeriod(
            new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        var gateway = new FakeGateway(busy);
        var service = CreateService(gateway, Now);

        await Assert.ThrowsAsync<SlotUnavailableException>(() => service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 9, 45, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 11, 2, 10, 15, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default));

        Assert.Empty(gateway.Created);
    }

    [Fact]
    public async Task Subito_dopo_il_buffer_l_intervallo_e_di_nuovo_libero()
    {
        var busy = new BusyPeriod(
            new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        var gateway = new FakeGateway(busy);
        var service = CreateService(gateway, Now);

        await service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 11, 15, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 11, 2, 12, 15, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default);

        Assert.Single(gateway.Created);
    }

    [Fact]
    public async Task Se_il_calendario_rifiuta_la_scrittura_la_risposta_e_un_conflitto()
    {
        var gateway = new FakeGateway { AcceptWrites = false };
        var service = CreateService(gateway, Now);

        await Assert.ThrowsAsync<SlotUnavailableException>(() => service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default));
    }

    [Fact]
    public async Task Con_24_ore_di_preavviso_oggi_non_e_prenotabile_ma_domani_si()
    {
        // Lunedì 2 novembre, 08:00 UTC = 09:00 a Roma.
        var gateway = new FakeGateway();
        var service = CreateService(gateway, new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero));

        await Assert.ThrowsAsync<BookingValidationException>(() => service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default));

        await service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default);

        Assert.Single(gateway.Created);
    }

    [Theory]
    [InlineData("2026-11-02T08:59:00+01:00", "2026-11-02T09:45:00+01:00")]
    [InlineData("2026-11-02T17:15:00+01:00", "2026-11-02T18:15:00+01:00")]
    [InlineData("2026-11-02T09:00:00+01:00", "2026-11-02T09:00:00+01:00")]
    [InlineData("2026-11-02T11:00:00+01:00", "2026-11-02T10:00:00+01:00")]
    [InlineData("2026-11-02T09:00:00+01:00", "2026-11-02T09:10:00+01:00")]
    [InlineData("2026-11-02T23:00:00+01:00", "2026-11-03T00:00:00+01:00")]
    public async Task Una_finestra_fuori_dalle_regole_viene_rifiutata(string start, string end)
    {
        var service = CreateService(new FakeGateway(), Now);

        await Assert.ThrowsAsync<BookingValidationException>(() => service.BookAsync(new BookingRequest
        {
            Start = DateTimeOffset.Parse(start),
            End = DateTimeOffset.Parse(end),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default));
    }

    [Fact]
    public async Task Il_sabato_non_e_prenotabile()
    {
        var service = CreateService(new FakeGateway(), Now);

        await Assert.ThrowsAsync<BookingValidationException>(() => service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 10, 31, 11, 0, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default));
    }

    [Fact]
    public async Task Oltre_l_orizzonte_non_si_prenota()
    {
        var service = CreateService(new FakeGateway(), Now);

        await Assert.ThrowsAsync<BookingValidationException>(() => service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2027, 6, 1, 10, 0, 0, TimeSpan.FromHours(2)),
            End = new DateTimeOffset(2027, 6, 1, 11, 0, 0, TimeSpan.FromHours(2)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default));
    }

    [Fact]
    public async Task L_ora_locale_d_inverno_ha_l_offset_di_un_ora()
    {
        var gateway = new FakeGateway();
        var service = CreateService(gateway, new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));

        await service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.FromHours(1)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default);

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero), Assert.Single(gateway.Created).Start);
    }

    [Fact]
    public async Task L_ora_locale_d_estate_ha_l_offset_di_due_ore()
    {
        var gateway = new FakeGateway();
        var service = CreateService(gateway, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));

        await service.BookAsync(new BookingRequest
        {
            Start = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.FromHours(2)),
            End = new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.FromHours(2)),
            Name = "Mario Rossi",
            Email = "mario@example.com",
            MeetingUrl = "https://meet.google.com/abc-defg-hij",
        }, default);

        Assert.Equal(new DateTimeOffset(2026, 10, 23, 7, 0, 0, TimeSpan.Zero), Assert.Single(gateway.Created).Start);
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
            End = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1)),
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
            End = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1)),
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
            () => service.GetBusyAsync(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), default));
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
        public DateTimeOffset LastFromUtc { get; private set; }
        public DateTimeOffset LastToUtc { get; private set; }

        public Task<IReadOnlyList<BusyPeriod>> GetBusyAsync(
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            LastFromUtc = fromUtc;
            LastToUtc = toUtc;
            return Task.FromResult<IReadOnlyList<BusyPeriod>>(busy);
        }

        public Task<bool> TryCreateEventAsync(CalendarBooking booking, CancellationToken ct)
        {
            if (!AcceptWrites) return Task.FromResult(false);
            Created.Add(booking);
            return Task.FromResult(true);
        }
    }
}
