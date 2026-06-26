using System.Security.Claims;
using MainSite.Blog;

namespace MainSite.Tests;

public sealed class BlogAuthStateTests
{
    [Fact]
    public void OAuth_configurato_abilita_qualunque_casella()
    {
        var state = new BlogAuthState(
            new BlogAuthOptions { ClientId = "id", ClientSecret = "secret" },
            isDevelopment: false);

        Assert.True(state.Configured);
        Assert.True(state.Enabled);
        Assert.True(state.IsAuthor(Principal("chiunque@example.com", authorClaim: true)));
    }

    [Fact]
    public void Senza_client_OAuth_l_accesso_resta_spento()
    {
        var state = new BlogAuthState(new BlogAuthOptions(), isDevelopment: false);

        Assert.False(state.Configured);
        Assert.False(state.Enabled);
    }

    [Fact]
    public void L_accesso_finto_vale_solo_in_sviluppo()
    {
        var options = new BlogAuthOptions { UseFake = true };
        var development = new BlogAuthState(options, isDevelopment: true);
        var production = new BlogAuthState(options, isDevelopment: false);

        Assert.True(development.UseFake);
        Assert.True(development.Enabled);
        Assert.True(development.IsAuthor(Principal(development.FakeAuthor, authorClaim: true)));

        Assert.False(production.UseFake);
        Assert.False(production.Enabled);
    }

    [Fact]
    public void IsAuthor_richiede_il_claim_messo_dopo_mailcow()
    {
        var state = new BlogAuthState(
            new BlogAuthOptions { ClientId = "id", ClientSecret = "secret" },
            isDevelopment: false);

        Assert.True(state.IsAuthor(Principal("chiunque@example.com", authorClaim: true)));
        Assert.False(state.IsAuthor(Principal("chiunque@example.com", authorClaim: false)));
        Assert.False(state.IsAuthor(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private static ClaimsPrincipal Principal(string email, bool authorClaim)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, email),
            new(ClaimTypes.NameIdentifier, email),
        };
        if (authorClaim)
            claims.Add(new Claim(BlogAuth.AuthorClaim, "true"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookie"));
    }
}
