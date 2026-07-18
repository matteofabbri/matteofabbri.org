namespace Maddalena.Licensing;

/// <summary>
/// The issuer side, used by the license web site — the sole issuer. It turns an <see cref="IssueSpec"/> into
/// a signed, <b>portable</b> token (the "key"): the entitlement (customer, kind, node count, validity) comes
/// from the spec, and a fresh license id is generated per issue. The license is binary — it does not enable
/// specific features — and the key is not bound to any machine. This is the one place the issuing logic lives.
/// </summary>
public static class LicenseIssuer
{
    /// <summary>Builds and signs a portable token (the "key") under the terms of <paramref name="spec"/>.</summary>
    public static string Issue(IssueSpec spec, string privateKeyBase64, string keyId)
    {
        var now = DateTimeOffset.UtcNow;
        var entitlement = new LicenseEntitlement(
            Guid.NewGuid().ToString("N"), spec.Customer, spec.Kind, spec.MaxNodes, now, now.AddDays(spec.Days));
        return LicenseSigner.Sign(entitlement, privateKeyBase64, keyId);
    }
}

/// <summary>
/// The terms to put in a license: who it is for, the <see cref="Kind"/> label (commercial/non-profit/demo/…
/// — a classification, never a capability switch), how many nodes, how many days.
/// </summary>
public sealed record IssueSpec(string Customer, string Kind, int MaxNodes, int Days);
