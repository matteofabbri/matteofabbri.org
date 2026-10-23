using System.Security.Claims;
using MainSite.Blog;

namespace MainSite.Tests;

public sealed class BlogAuthStateTests
{
    [Fact]
    public void Una_casella_in_elenco_e_ammessa_senza_distinzione_di_maiuscole()
    {
        var state = new BlogAuthState(
            new BlogAuthOptions { ClientId = "id", ClientSecret = "secret" },
            isDevelopment: false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "matteo@maddalena.me" });

        Assert.True(state.IsAllowed("Matteo@Maddalena.ME"));
        Assert.False(state.IsAllowed("altro@example.com"));
        Assert.False(state.IsAllowed(null));
    }

    [Fact]
    public void L_accesso_finto_vale_solo_in_sviluppo_e_ammette_l_autore_di_sviluppo()
    {
        var options = new BlogAuthOptions { UseFake = true };
        var development = new BlogAuthState(options, isDevelopment: true, new HashSet<string>());
        var production = new BlogAuthState(options, isDevelopment: false, new HashSet<string>());

        Assert.True(development.UseFake);
        Assert.True(development.Enabled);
        Assert.True(development.IsAllowed(development.FakeAuthor));

        Assert.False(production.UseFake);
        Assert.False(production.Enabled);
        Assert.False(production.IsAllowed("sviluppo@localhost"));
    }

    [Fact]
    public void OAuth_configurato_ma_senza_autori_non_abilita_l_accesso()
    {
        var state = new BlogAuthState(
            new BlogAuthOptions { ClientId = "id", ClientSecret = "secret" },
            isDevelopment: false,
            new HashSet<string>());

        Assert.True(state.Configured);
        Assert.False(state.Enabled);
    }

    [Fact]
    public void IsAuthor_richiede_il_claim_e_la_casella_ancora_in_elenco()
    {
        var state = new BlogAuthState(
            new BlogAuthOptions { ClientId = "id", ClientSecret = "secret" },
            isDevelopment: false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "matteo@maddalena.me" });

        Assert.True(state.IsAuthor(Principal("matteo@maddalena.me", authorClaim: true)));
        Assert.False(state.IsAuthor(Principal("altro@example.com", authorClaim: true)));
        Assert.False(state.IsAuthor(Principal("matteo@maddalena.me", authorClaim: false)));
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
