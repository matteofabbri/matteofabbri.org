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
    // GET /api/booking/availability?from=2026-11-01&to=2026-11-30
    // Senza parametri: i prossimi Booking:DefaultDays giorni.
    [HttpGet("availability")]
    public async Task<IActionResult> GetAvailability(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        try
        {
            return Ok(await booking.GetAvailabilityAsync(from, to, ct));
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
    // { "start": "2026-11-03T10:00:00+01:00", "name": "...", "email": "...", "note": "...",
    //   "meetingUrl": "https://meet.google.com/..." }
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
