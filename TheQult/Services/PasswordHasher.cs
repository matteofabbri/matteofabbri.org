using System.Security.Cryptography;

namespace TheQult.Services;

// Hash delle password con PBKDF2 (SHA-512) e salt per utente. Il formato è
// autodescrittivo ("pbkdf2-sha512.v1$iterazioni$salt$hash"): il giorno in cui
// serve salire di versione le hash vecchie restano verificabili una a una.
public static class PasswordHasher
{
    // OWASP per PBKDF2-HMAC-SHA512: 210.000 iterazioni.
    public const int Iterations = 210_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const string Prefix = "pbkdf2-sha512.v1";

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA512, HashSizeBytes);

        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    // Confronto a tempo costante sul byte finale: la lunghezza attesa viene
    // dal formato memorizzato, non dal confronto.
    public static bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded))
            return false;

        string[] parts = encoded.Split('$');
        if (parts.Length != 4
            || parts[0] != Prefix
            || !int.TryParse(parts[1], out int iterations)
            || iterations < 1)
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
