namespace Maddalena.Data;

/// <summary>
/// A commercial license bought by an account, held in Postgres next to the Identity user. The signed,
/// portable key (<see cref="Token"/>) is what the buyer downloads; keeping it per-account lets them
/// re-download it later. Honor model: the key is a receipt, not a machine lock.
/// </summary>
public sealed class AccountLicense
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";          // IdentityUser.Id of the buyer
    public string StripeSessionId { get; set; } = "";  // the checkout that paid for it (dedupe + success lookup)
    public string Customer { get; set; } = "";
    public string Kind { get; set; } = "";             // free label (commercial/…), not a capability
    public int MaxNodes { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Token { get; set; } = "";            // the signed, portable key
}
