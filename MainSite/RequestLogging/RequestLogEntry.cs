using System.Text.Json.Serialization;

namespace MainSite.RequestLogging;

// Una richiesta HTTP registrata. Il payload finisce in Sperone così com'è
// (JSON camelCase): non c'è uno schema sul server, quindi aggiungere un campo
// qui non richiede migrazioni.
public sealed record RequestLogEntry
{
    // ItemId di Sperone: ordinabile per tempo (tick UTC + suffisso casuale).
    public required string Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    // --- richiesta -------------------------------------------------------
    public required string Method { get; init; }
    public required string Scheme { get; init; }
    public required string Host { get; init; }
    public required string Path { get; init; }
    public string? QueryString { get; init; }
    public string? Protocol { get; init; }
    public bool IsHttps { get; init; }

    // --- risposta --------------------------------------------------------
    public int StatusCode { get; init; }
    public double DurationMs { get; init; }

    // --- client ----------------------------------------------------------
    public string? RemoteIp { get; init; }
    public int? RemotePort { get; init; }

    // Header grezzo X-Forwarded-For (dietro Caddy contiene la catena dei proxy).
    public string? ForwardedFor { get; init; }

    public string? UserAgent { get; init; }
    public string? Client { get; init; }
    public string? Browser { get; init; }
    public string? BrowserVersion { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OperatingSystemVersion { get; init; }
    public string? DeviceType { get; init; }
    public bool IsBot { get; init; }

    // Paese dell'IP (ISO 3166-1 alpha-2, es. "IT"; vuoto = privato o non
    // geolocalizzato). Il codice è indicizzato in Sperone; il nome è comodo
    // nel payload per la lista delle richieste.
    public string CountryCode { get; init; } = "";
    public string? CountryName { get; init; }

    public string? Referer { get; init; }
    public string? AcceptLanguage { get; init; }

    // --- contenuto -------------------------------------------------------
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }

    // --- identità e correlazione ----------------------------------------
    public string? User { get; init; }
    public string? RequestId { get; init; }
    public string? TraceId { get; init; }

    // Nome del servizio: oggi è sempre "site", ma tiene la porta aperta a
    // TheQult/Maddalena nella stessa collezione.
    public string Service { get; init; } = "site";
}

// Serializzazione source-generated: nessuna reflection a runtime (Sperone
// consiglia un JsonSerializerContext, e il sito resta trim/AOT-friendly).
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RequestLogEntry))]
internal sealed partial class RequestLogJsonContext : JsonSerializerContext;
