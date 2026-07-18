using Maddalena.Licensing;

namespace Maddalena.Licensing.Tests;

public class LicenseTests
{
    private static LicenseEntitlement Sample() => new(
        LicenseId: "lic-001", Customer: "Acme Inc.", Kind: "commercial", MaxNodes: 3,
        IssuedAt: DateTimeOffset.UtcNow, NotAfter: DateTimeOffset.UtcNow.AddYears(1));

    private static Dictionary<string, string> Keys(string keyId, string publicKey) => new() { [keyId] = publicKey };

    [Fact]
    public void Sign_then_verify_returns_the_same_entitlement()
    {
        var (pub, priv) = LicenseSigner.GenerateKeyPair();
        var ent = Sample();

        string token = LicenseSigner.Sign(ent, priv, keyId: "k1");

        Assert.True(License.TryVerify(token, Keys("k1", pub), out var back));
        Assert.Equal(ent.LicenseId, back.LicenseId);
        Assert.Equal(ent.Customer, back.Customer);
        Assert.Equal(ent.Kind, back.Kind);
        Assert.Equal(ent.MaxNodes, back.MaxNodes);
        Assert.Equal(ent.IssuedAt, back.IssuedAt);
        Assert.Equal(ent.NotAfter, back.NotAfter);
    }

    [Fact]
    public void A_tampered_token_fails_verification()
    {
        var (pub, priv) = LicenseSigner.GenerateKeyPair();
        string token = LicenseSigner.Sign(Sample(), priv, "k1");

        var chars = token.ToCharArray();
        int i = token.Length / 2;
        chars[i] = chars[i] == 'A' ? 'B' : 'A';

        Assert.False(License.TryVerify(new string(chars), Keys("k1", pub), out _));
    }

    [Fact]
    public void A_token_signed_with_another_key_fails_verification()
    {
        var (_, priv) = LicenseSigner.GenerateKeyPair();
        var (otherPub, _) = LicenseSigner.GenerateKeyPair();

        string token = LicenseSigner.Sign(Sample(), priv, "k1");

        Assert.False(License.TryVerify(token, Keys("k1", otherPub), out _));
    }

    [Fact]
    public void An_unknown_key_id_is_rejected()
    {
        var (pub, priv) = LicenseSigner.GenerateKeyPair();
        string token = LicenseSigner.Sign(Sample(), priv, "k1");

        // the server only knows "k2": the token's "k1" cannot be resolved
        Assert.False(License.TryVerify(token, Keys("k2", pub), out _));
    }

    [Fact]
    public void Rotation_an_old_token_still_verifies_while_both_keys_are_known()
    {
        var (pubOld, privOld) = LicenseSigner.GenerateKeyPair();
        var (pubNew, _) = LicenseSigner.GenerateKeyPair();

        string oldToken = LicenseSigner.Sign(Sample(), privOld, "k1");

        // server holds both the old and the new public key during rotation
        var keys = new Dictionary<string, string> { ["k1"] = pubOld, ["k2"] = pubNew };
        Assert.True(License.TryVerify(oldToken, keys, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"keyId":"k1","payload":"@@@","signature":"@@@"}""")]
    public void Malformed_tokens_are_rejected(string token)
    {
        var (pub, _) = LicenseSigner.GenerateKeyPair();
        Assert.False(License.TryVerify(token, Keys("k1", pub), out _));
    }

    [Fact]
    public void A_malformed_public_key_does_not_throw_and_fails()
    {
        var (_, priv) = LicenseSigner.GenerateKeyPair();
        string token = LicenseSigner.Sign(Sample(), priv, "k1");
        Assert.False(License.TryVerify(token, Keys("k1", "not-a-key"), out _));
    }

    [Fact]
    public void Issuer_produces_a_portable_token_that_verifies()
    {
        var (pub, priv) = LicenseSigner.GenerateKeyPair();
        var spec = new IssueSpec("Customer", "commercial", MaxNodes: 5, Days: 30);

        string token = LicenseIssuer.Issue(spec, priv, "k1");

        Assert.True(License.TryVerify(token, Keys("k1", pub), out var e));
        Assert.Equal("Customer", e.Customer);            // from the spec
        Assert.Equal("commercial", e.Kind);              // from the spec
        Assert.Equal(5, e.MaxNodes);                     // from the spec
        Assert.False(string.IsNullOrEmpty(e.LicenseId));  // generated per issue
        Assert.InRange((e.NotAfter - e.IssuedAt).TotalDays, 29.9, 30.1);
    }
}
