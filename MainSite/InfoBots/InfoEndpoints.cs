using System.Net;
using System.Text;
using MainSite.RequestLogging;
using Microsoft.AspNetCore.Http.Features;

namespace MainSite.InfoBots;

// Endpoint pubblico per raccogliere i payload dei bot (o di chiunque faccia
// POST qui). Il body è letto come stringa grezza e salvato nella collezione
// Sperone "info" con un Guid, l'IP e il paese.
public static class InfoEndpoints
{
    private const int MaxBodyBytes = 1_000_000;

    public static void MapInfoEndpoints(this WebApplication app)
    {
        app.MapPost("/info-send", async (
            HttpContext context,
            RequestLogStore store,
            ICountryResolver countryResolver,
            CancellationToken ct) =>
        {
            if (context.Request.ContentLength is > MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = MaxBodyBytes;

            string payload;
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                payload = await reader.ReadToEndAsync(ct);
            }
            catch (BadHttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            IPAddress? address = context.Connection.RemoteIpAddress;
            if (address is { IsIPv4MappedToIPv6: true })
                address = address.MapToIPv4();

            var country = countryResolver.Resolve(address);
            var entry = await store.InsertInfoAsync(
                payload,
                address?.ToString(),
                country.Code,
                country.IsKnown ? country.Name : null,
                ct);

            return entry is null
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(new { id = entry.Guid });
        }).RequireRateLimiting("info-send");
    }
}
