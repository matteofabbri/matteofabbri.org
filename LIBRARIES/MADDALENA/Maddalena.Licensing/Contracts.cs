namespace Maddalena.Licensing;

/// <summary>
/// What a license grants, signed into a portable token (the "key"). It is the token payload: the site signs
/// it and the status reader on the node verifies it. The license is <b>binary</b> — a valid, unexpired token
/// is a receipt for the whole product; there is NO per-feature or per-edition grant. In the honor model the
/// token never stops the server: <see cref="NotAfter"/> only drives the <i>reported</i> status. The key is
/// <b>portable</b> — it is not bound to any machine. <see cref="MaxNodes"/> is a contractual figure; the node
/// count is not enforced at runtime.
/// <para>
/// <see cref="Kind"/> is a free-form <b>label</b> (e.g. <c>commercial</c>, <c>non-profit</c>, <c>demo</c>,
/// <c>datacenter</c>) — it classifies the license for display, billing and records. It is NOT a capability
/// switch: behavior is identical for every kind, and "how big" is the <see cref="MaxNodes"/> number, not the
/// kind. New kinds can be invented at issuance with no format or server change. Keeping it a label (never an
/// edition) is what preserves the binary model.
/// </para>
/// </summary>
public sealed record LicenseEntitlement(
    string LicenseId,
    string Customer,
    string Kind,
    int MaxNodes,
    DateTimeOffset IssuedAt,
    DateTimeOffset NotAfter);
