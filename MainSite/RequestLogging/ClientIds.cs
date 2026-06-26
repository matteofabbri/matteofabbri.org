using System.Security.Cryptography;
using System.Text;

namespace MainSite.RequestLogging;

// Identificativo "di client" senza cookie e senza parsing dello User-Agent:
// hash troncato di IP + User-Agent. Stesso IP e stesso UA = stesso id; cambiare
// UA (o IP) produce un id diverso. 16 caratteri esadecimali = 64 bit.
public static class ClientIds
{
    public static string? For(string? remoteIp, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(remoteIp) && string.IsNullOrWhiteSpace(userAgent))
            return null;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{remoteIp}\n{userAgent}"));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
