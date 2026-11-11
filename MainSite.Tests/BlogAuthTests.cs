using System.Security.Claims;
using MainSite.Auth;
using MainSite.Blog;

namespace MainSite.Tests;

public sealed class BlogAuthTests
{
    [Fact]
    public void IsAuthor_requires_the_author_claim()
    {
        Assert.True(BlogAuth.IsAuthor(Principal(authorClaim: true)));
        Assert.False(BlogAuth.IsAuthor(Principal(authorClaim: false)));
        Assert.False(BlogAuth.IsAuthor(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void CreatePrincipal_carries_the_account_identity_and_stamp()
    {
        var credentials = new LocalUserCredentials(
            7, "matteo", "Matteo Fabbri", "matteo@example.com", "hash", "stamp-1", true);

        var principal = BlogAuth.CreatePrincipal(credentials);

        Assert.Equal("7", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("Matteo Fabbri", principal.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Equal("matteo@example.com", principal.FindFirst(ClaimTypes.Email)?.Value);
        Assert.Equal("stamp-1", principal.FindFirst(BlogAuth.SecurityStampClaim)?.Value);
        Assert.True(BlogAuth.IsAuthor(principal));
    }

    private static ClaimsPrincipal Principal(bool authorClaim)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "1"),
            new(ClaimTypes.Name, "Test"),
        };
        if (authorClaim)
            claims.Add(new Claim(BlogAuth.AuthorClaim, "true"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
