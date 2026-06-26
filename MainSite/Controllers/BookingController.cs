using MainSite.Booking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MainSite.Controllers;

// Le API pubbliche delle prenotazioni. Nessuna autenticazione: chiunque può
// vedere gli orari liberi e prenotarne uno. Non escono mai dettagli degli
// impegni già in calendario, solo la disponibilità che ne deriva.
[ApiController]
[Route("api/booking")]
public sealed class BookingController(BookingService booking) : ControllerBase
{
    private TimeZoneInfo? _zone;
    private TimeZoneInfo Zone => _zone ??= TimeZoneInfo.FindSystemTimeZoneById(booking.TimeZoneId);

    // GET /api/booking/busy?from=2026-11-02&to=2026-11-08
    // Gli intervalli già occupati in calendario, spezzati per giorno e in ora
    // locale. Senza parametri: la settimana corrente.
    [HttpGet("busy")]
    public async Task<IActionResult> GetBusy(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime);
        var start = from ?? today;
        var end = to ?? start.AddDays(6);

        try
        {
            return Ok(new BusyResponse(await booking.GetBusyAsync(start, end, ct)));
        }
        catch (BookingValidationException ex)
        {
            return BadRequest(new ApiError(ex.Message));
        }
        catch (CalendarNotConfiguredException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError(ex.Message));
        }
        catch (CalendarUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ApiError(ex.Message));
        }
    }

    // POST /api/booking/appointments
    // { "start": "2026-11-03T10:00:00+01:00", "end": "2026-11-03T11:00:00+01:00",
    //   "name": "...", "email": "...", "note": "...", "meetingUrl": "https://..." }
    [HttpPost("appointments")]
    [EnableRateLimiting("booking")]
    public async Task<IActionResult> Book([FromBody] BookingRequest request, CancellationToken ct)
    {
        try
        {
            var created = await booking.BookAsync(request, ct);
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (BookingValidationException ex)
        {
            return BadRequest(new ApiError(ex.Message));
        }
        catch (SlotUnavailableException ex)
        {
            return Conflict(new ApiError(ex.Message));
        }
        catch (CalendarNotConfiguredException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError(ex.Message));
        }
        catch (CalendarUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ApiError(ex.Message));
        }
    }
}

public sealed record ApiError(string Error);

public sealed record BusyResponse(IReadOnlyList<BusyBlock> Blocks);
