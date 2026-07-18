using System.Text;
using Maddalena.Data;
using Maddalena.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Maddalena.Licensing;
using Stripe;
using Stripe.Checkout;

namespace Maddalena.Controllers;

/// <summary>
/// Licensing, hosted inside maddalena.me under /licensing. A logged-in customer buys a commercial
/// license via Stripe; the signature-verified webhook SIGNS a portable key and stores it on the
/// account. Honor model: the key is a receipt, not a machine lock — free users never come here.
/// The UI lives in the static pages (wwwroot/licensing*.html, licenses.html): this controller is
/// JSON endpoints under /api/licensing plus the two non-UI routes (key download, Stripe webhook).
/// </summary>
[ApiController]   // anche per l'ApiExplorer: senza, queste rotte non entrano in /openapi/v1.json
[Route("licensing")]
public sealed class LicensingController(
    ApplicationDbContext db,
    UserManager<IdentityUser> users,
    IConfiguration config,
    ILogger<LicensingController> log) : ControllerBase
{
    // A single SKU for now: a 1-year commercial license, priced inline (no dashboard product).
    private const long PriceCents = 9900;   // €99.00
    private const string Currency = "eur";

    // Starts a Stripe Checkout and returns its URL: it is the JS that navigates there.
    [Authorize]
    [HttpPost("~/api/licensing/buy")]
    [ProducesResponseType<BuyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Buy()
    {
        string userId = users.GetUserId(User)!;
        string domain = $"{Request.Scheme}://{Request.Host}";
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            ClientReferenceId = userId,   // ties the payment back to this account in the webhook
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = Currency,
                        UnitAmount = PriceCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = "Sperone commercial license (1 year)",
                        },
                    },
                },
            ],
            SuccessUrl = $"{domain}/licensing-success.html?session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl = $"{domain}/licensing-cancel.html",
        };
        var session = await new SessionService().CreateAsync(options);
        return Ok(new BuyResponse { Url = session.Url });
    }

    // The licenses of the current account, newest first.
    [Authorize]
    [HttpGet("~/api/licensing/mine")]
    [ProducesResponseType<IEnumerable<LicenseResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Mine()
    {
        string userId = users.GetUserId(User)!;
        var mine = await db.Licenses
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.IssuedAt)
            .ToListAsync();
        return Ok(mine.Select(l => new LicenseResponse
        {
            Id = l.Id,
            Kind = l.Kind,
            MaxNodes = l.MaxNodes,
            IssuedAt = l.IssuedAt,
            ExpiresAt = l.ExpiresAt,
            Expired = l.ExpiresAt < DateTime.UtcNow,
            DownloadUrl = $"/licensing/download/{l.Id}",
        }));
    }

    // Stripe redirects the buyer to the success page with a session_id; the grant itself is the
    // webhook's job. Here the page asks "is my key ready?": if the webhook has not run yet (a
    // moment's lag) ready=false and the page retries.
    [Authorize]
    [HttpGet("~/api/licensing/session")]
    [ProducesResponseType<CheckoutSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CheckoutSession(string? session_id)
    {
        string userId = users.GetUserId(User)!;
        var lic = string.IsNullOrEmpty(session_id)
            ? null
            : await db.Licenses.FirstOrDefaultAsync(l => l.StripeSessionId == session_id && l.UserId == userId);
        return Ok(new CheckoutSessionResponse
        {
            Ready = lic is not null,
            DownloadUrl = lic is null ? null : $"/licensing/download/{lic.Id}",
        });
    }

    // Re-download a key — only if it belongs to the current user. A plain navigable link
    // (not /api): an anonymous hit gets redirected to the static login page.
    [Authorize]
    [HttpGet("download/{id:int}")]
    public async Task<IActionResult> Download(int id)
    {
        string userId = users.GetUserId(User)!;
        var lic = await db.Licenses.FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId);
        if (lic is null)
            return NotFound();
        return File(Encoding.UTF8.GetBytes(lic.Token), "application/json", "license.json");
    }

    // Stripe → us: the authoritative "payment happened" signal (server-to-server, signature-verified). On a
    // completed checkout it signs a portable key and stores it on the buyer's account. Anonymous but verified:
    // Stripe calls it, not the browser. Documented in OpenAPI (every endpoint is), though it takes no JSON body.
    [AllowAnonymous]
    [HttpPost("sperone/webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Webhook()
    {
        string json = await new StreamReader(Request.Body).ReadToEndAsync();
        string? secret = config["Stripe:WebhookSecret"];
        if (string.IsNullOrEmpty(secret))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Webhook secret not configured.");

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(json, Request.Headers["Stripe-Signature"], secret);
        }
        catch (StripeException)
        {
            return BadRequest(); // bad signature or malformed: not from Stripe
        }

        if (stripeEvent.Type == "checkout.session.completed" && stripeEvent.Data.Object is Session session)
        {
            string? userId = session.ClientReferenceId;
            if (string.IsNullOrEmpty(userId))
                return Ok(); // not one of ours

            // Idempotent: Stripe may retry the same event. One key per checkout session.
            if (await db.Licenses.AnyAsync(l => l.StripeSessionId == session.Id))
                return Ok();

            string? privateKey = config["Licensing:PrivateKey"];
            string keyId = config["Licensing:KeyId"] ?? "k1";
            if (string.IsNullOrWhiteSpace(privateKey))
            {
                // Paid but we cannot sign: surface it (500 → Stripe retries) rather than dropping it.
                log.LogError("Paid checkout {SessionId} but no signing key configured (Licensing:PrivateKey).", session.Id);
                return StatusCode(StatusCodes.Status500InternalServerError, "No signing key configured.");
            }

            var spec = new IssueSpec(
                Customer: session.CustomerDetails?.Email ?? "",
                Kind: "commercial", MaxNodes: 1, Days: 365);
            string token = LicenseIssuer.Issue(spec, privateKey, keyId);

            db.Licenses.Add(new AccountLicense
            {
                UserId = userId,
                StripeSessionId = session.Id,
                Customer = spec.Customer,
                Kind = spec.Kind,
                MaxNodes = spec.MaxNodes,
                IssuedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(spec.Days),
                Token = token,
            });
            await db.SaveChangesAsync();
            log.LogInformation("Issued a commercial key for {UserId} (checkout {SessionId}).", userId, session.Id);
        }

        return Ok();
    }

    // Dev-only: mint a fresh signing key pair to bootstrap the vendor keys — the PUBLIC half goes into the
    // product (EmbeddedLicenseKeys), the PRIVATE half into Licensing:PrivateKey. Never outside Development.
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("keygen")]
    public IActionResult Keygen([FromServices] IWebHostEnvironment env)
    {
        if (!env.IsDevelopment())
            return NotFound();
        var (publicKey, privateKey) = LicenseSigner.GenerateKeyPair();
        return Content(
            $"public:  {publicKey}\nprivate: {privateKey}\n\n" +
            "Embed the PUBLIC key in the product (EmbeddedLicenseKeys); set the PRIVATE key as Licensing:PrivateKey.",
            "text/plain");
    }
}
