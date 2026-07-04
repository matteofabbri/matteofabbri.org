namespace Maddalena.Model;

public sealed class LicenseResponse
{
    public int Id { get; set; }

    public string Kind { get; set; } = "";

    public int MaxNodes { get; set; }

    public DateTime IssuedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public bool Expired { get; set; }

    public string DownloadUrl { get; set; } = "";
}
