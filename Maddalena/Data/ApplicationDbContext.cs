using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Maddalena.Data;

// Un solo contesto EF per Maddalena: gli account ASP.NET Core Identity + le licenze
// commerciali. (Nel DB di produzione restano tabelle OpenIddict orfane dell'epoca in
// cui Maddalena era anche provider OIDC: innocue, da droppare a mano volendo.)
public class ApplicationDbContext : IdentityDbContext<IdentityUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    // Commercial licenses bought through the site, one row per purchase, linked to the buyer's account.
    public DbSet<AccountLicense> Licenses => Set<AccountLicense>();
}
