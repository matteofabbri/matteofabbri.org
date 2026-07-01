using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Maddalena.Data;

// Un solo contesto EF per Maddalena: gli account ASP.NET Core Identity + le tabelle
// di OpenIddict (applicazioni client, autorizzazioni, token). Le tabelle OpenIddict
// vengono registrate nel modello da `options.UseOpenIddict()` nell'AppHost/Program.
public class ApplicationDbContext : IdentityDbContext<IdentityUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    // Commercial licenses bought through the site, one row per purchase, linked to the buyer's account.
    public DbSet<AccountLicense> Licenses => Set<AccountLicense>();
}
