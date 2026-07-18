using System.Text.Json.Serialization;

namespace Maddalena.Licensing;

/// <summary>
/// The licensing JSON contract, generated at compile time (no reflection: AOT requirement). It serializes
/// the token payload and the signed envelope.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LicenseEntitlement))]
[JsonSerializable(typeof(TokenEnvelope))]
internal sealed partial class LicensingJson : JsonSerializerContext;

/// <summary>
/// The token envelope: which key signed it (<see cref="KeyId"/>, so the verifier can hold several public
/// keys and rotate), the signed payload (the exact entitlement JSON bytes, base64) and the signature (base64).
/// </summary>
internal sealed record TokenEnvelope(string KeyId, string Payload, string Signature);
