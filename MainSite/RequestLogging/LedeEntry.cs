using System.Text.Json.Serialization;

namespace MainSite.RequestLogging;

// Una frase breve mostrata a caso nel jumbotron della home. Vivono nella
// collezione Sperone "ledes" e si gestiscono da /admin/ledes.
public sealed record LedeEntry
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LedeEntry))]
internal sealed partial class LedeJsonContext : JsonSerializerContext;
