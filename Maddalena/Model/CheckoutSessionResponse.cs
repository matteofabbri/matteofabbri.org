namespace Maddalena.Model;

public sealed class CheckoutSessionResponse
{
    public bool Ready { get; set; }

    public string? DownloadUrl { get; set; }
}
