using System.Text.Json.Serialization;

namespace MainSite.InfoBots;

// Un payload arrivato su POST /info-send: body grezzo, IP e paese del
// chiamante, più l'id generato al momento della ricezione. Sta nella stessa
// istanza Sperone dei log richieste (collezione "info").
public sealed record InfoEntry
{
    // ItemId ordinabile per tempo (tick UTC + Guid): la pagina legge le
    // ultime righe senza un indice secondario.
    public required string Id { get; init; }

    public required Guid Guid { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Payload { get; init; }
    public string? RemoteIp { get; init; }
    public string CountryCode { get; init; } = "";
    public string? CountryName { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InfoEntry))]
internal sealed partial class InfoJsonContext : JsonSerializerContext;
