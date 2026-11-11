using System.Net.Mail;
using System.Text.RegularExpressions;

namespace MainSite.Auth;

// Validation and normalization shared by the bootstrap, the login form and the
// account-management page.
public static partial class LocalUserRules
{
    public const int MaxUsernameLength = 64;
    public const int MaxDisplayNameLength = 100;
    public const int MaxEmailLength = 200;
    public const int MaxPasswordLength = 256;

    public static string NormalizeUsername(string? value) =>
        (value ?? "").Trim().ToLowerInvariant();

    public static bool IsValidUsername(string username) =>
        username.Length is >= 3 and <= MaxUsernameLength
        && UsernamePattern().IsMatch(username);

    public static string? ValidateUsername(string? raw)
    {
        string username = NormalizeUsername(raw);
        return IsValidUsername(username)
            ? null
            : "The username must be 3-64 characters: letters, digits, dot, dash or underscore, starting with a letter or digit.";
    }

    public static string? ValidateDisplayName(string? raw)
    {
        string value = (raw ?? "").Trim();
        if (value.Length == 0)
            return "The display name is required.";
        if (value.Length > MaxDisplayNameLength)
            return $"The display name cannot exceed {MaxDisplayNameLength} characters.";
        return null;
    }

    public static string? ValidateEmail(string? raw)
    {
        string value = (raw ?? "").Trim();
        if (value.Length == 0)
            return null;
        if (value.Length > MaxEmailLength)
            return $"The email address cannot exceed {MaxEmailLength} characters.";
        return MailAddress.TryCreate(value, out var address)
            && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase)
            ? null
            : "The email address is not valid.";
    }

    public static string? ValidatePassword(string? raw, int minLength)
    {
        int minimum = Math.Max(8, minLength);
        string value = raw ?? "";
        if (value.Length == 0)
            return "The password is required.";
        if (value.Length < minimum)
            return $"The password must be at least {minimum} characters.";
        if (value.Length > MaxPasswordLength)
            return $"The password cannot exceed {MaxPasswordLength} characters.";
        if (string.IsNullOrWhiteSpace(value))
            return "The password cannot be only whitespace.";
        return null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
