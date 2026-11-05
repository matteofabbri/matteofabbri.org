using System.Text.Json.Serialization;

namespace TheQult.Data;

// Serializzazione source-generated: nessuna reflection a runtime (Sperone
// consiglia un JsonSerializerContext, e il sito resta trim/AOT-friendly).
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Member))]
[JsonSerializable(typeof(StayRequest))]
[JsonSerializable(typeof(Reference))]
internal sealed partial class TheQultJsonContext : JsonSerializerContext;
