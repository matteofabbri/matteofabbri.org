using TheQult.Services;

namespace TheQult.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void HashThenVerify_AcceptsTheRightPassword()
    {
        string encoded = PasswordHasher.Hash("couch-and-coffee");

        Assert.True(PasswordHasher.Verify("couch-and-coffee", encoded));
    }

    [Fact]
    public void Verify_RejectsTheWrongPassword()
    {
        string encoded = PasswordHasher.Hash("couch-and-coffee");

        Assert.False(PasswordHasher.Verify("couch-and-coffee2", encoded));
        Assert.False(PasswordHasher.Verify("", encoded));
        Assert.False(PasswordHasher.Verify("couch-and-coffee", ""));
    }

    [Fact]
    public void TwoHashesOfTheSamePassword_Differ_SoSaltsAreUnique()
    {
        string first = PasswordHasher.Hash("same-password");
        string second = PasswordHasher.Hash("same-password");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("same-password", first));
        Assert.True(PasswordHasher.Verify("same-password", second));
    }

    [Fact]
    public void Verify_RejectsGarbageInsteadOfThrowing()
    {
        Assert.False(PasswordHasher.Verify("x", "not-a-hash"));
        Assert.False(PasswordHasher.Verify("x", "pbkdf2-sha512.v1$abc$not-base64$also-not"));
        Assert.False(PasswordHasher.Verify("x", "sha256$1$salt$hash"));
    }
}
