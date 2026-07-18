using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace Maddalena.Licensing;

/// <summary>
/// The <b>verification</b> side of licensing — the only part a product server (customer-facing) needs. It
/// verifies a token against the public keys embedded in the server. There is no signing or key generation
/// here: that is the issuer's job and lives in <c>Maddalena.Licensing.Issuing</c> (never shipped to
/// customers). Verification is offline and checks only authenticity and integrity; expiry drives the
/// reported status.
/// </summary>
public static class License
{
    /// <summary>
    /// Verifies a <paramref name="token"/>: it picks the public key from <paramref name="publicKeysByKeyId"/>
    /// by the token's key id, then checks the signature. True (and returns the <paramref name="entitlement"/>)
    /// only if the key id is known and the signature and integrity hold. It does not check expiry or the
    /// node — that is the gate's job. Any malformed, tampered, unknown-key or wrong-key token returns false.
    /// </summary>
    public static bool TryVerify(string token, IReadOnlyDictionary<string, string> publicKeysByKeyId, [NotNullWhen(true)] out LicenseEntitlement? entitlement)
    {
        entitlement = null;

        TokenEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize(token, LicensingJson.Default.TokenEnvelope); }
        catch (JsonException) { return false; }
        if (envelope is null || string.IsNullOrEmpty(envelope.KeyId)
            || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature))
            return false;

        if (!publicKeysByKeyId.TryGetValue(envelope.KeyId, out var publicKeyBase64))
            return false; // unknown key id

        byte[] payload, signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException) { return false; }

        using var ecdsa = ECDsa.Create();
        try { ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _); }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }

        if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256))
            return false;

        try { entitlement = JsonSerializer.Deserialize(payload, LicensingJson.Default.LicenseEntitlement); }
        catch (JsonException) { return false; }
        return entitlement is not null;
    }

}
