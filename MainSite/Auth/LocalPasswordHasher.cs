using System.Globalization;
using System.Security.Cryptography;

namespace MainSite.Auth;

// PBKDF2-HMAC-SHA256 with a per-password salt and a versioned, self-describing
// format: pbkdf2-sha256$iterations$salt$hash. Verification is constant-time.
public static class LocalPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // Verified against a wrong password when the username does not exist, so a
    // missing account and a wrong password cost the same time.
    public static string DummyHash { get; } = Hash("dummy-password-for-constant-time");

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return string.Concat(
            Prefix, "$",
            Iterations.ToString(CultureInfo.InvariantCulture), "$",
            Convert.ToBase64String(salt), "$",
            Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string stored)
    {
        string[] parts = (stored ?? "").Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations)
            || iterations is < 1 or > 2_000_000)
        {
            return false;
        }

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

        if (salt.Length == 0 || expected.Length == 0)
            return false;

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
