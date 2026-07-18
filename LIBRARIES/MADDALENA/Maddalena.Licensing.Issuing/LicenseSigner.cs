using System.Security.Cryptography;
using System.Text.Json;

namespace Maddalena.Licensing;

/// <summary>
/// The signing side of licensing — vendor-only (the license site). It
/// generates key pairs and signs a <see cref="LicenseEntitlement"/> into a token. It lives in a separate
/// library so a customer-facing product server never ships the signing or key-generation code; the server
/// only references <c>Maddalena.Licensing</c> and gets verification (<see cref="License.TryVerify"/>).
/// Signatures are ECDSA P-256 over SHA-256, over the same canonical token format the verifier reads.
/// </summary>
public static class LicenseSigner
{
    /// <summary>
    /// Generates an ECDSA P-256 key pair. The <c>PublicKey</c> (SubjectPublicKeyInfo, base64) is embedded in
    /// the server; the <c>PrivateKey</c> (PKCS#8, base64) stays secret on the issuer side (HSM/secret store),
    /// <b>never</b> in the repository.
    /// </summary>
    public static (string PublicKey, string PrivateKey) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
    }

    /// <summary>
    /// Signs <paramref name="entitlement"/> with the private key and produces the token (to be written into
    /// the activation file). <paramref name="keyId"/> labels which key signed it, so the server can hold
    /// several public keys and rotate without breaking tokens already issued.
    /// </summary>
    public static string Sign(LicenseEntitlement entitlement, string privateKeyBase64, string keyId)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(entitlement, LicensingJson.Default.LicenseEntitlement);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        byte[] signature = ecdsa.SignData(payload, HashAlgorithmName.SHA256);

        var envelope = new TokenEnvelope(keyId, Convert.ToBase64String(payload), Convert.ToBase64String(signature));
        return JsonSerializer.Serialize(envelope, LicensingJson.Default.TokenEnvelope);
    }
}
