using System.Globalization;
using System.Security.Claims;
using MainSite.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MainSite.Blog;

// Author sign-in with LOCAL accounts: MainSite verifies the password against the
// SQLite user store and the browser keeps only the cookie below. No external
// identity provider is involved.
public static class BlogAuth
{
    public const string Policy = "BlogAuthor";
    public const string AuthorClaim = "blog_author";
    public const string SecurityStampClaim = "local_stamp";
    public const string CookieName = "mf-blog-auth";

    public static bool IsAuthor(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.HasClaim(AuthorClaim, "true");

    public static ClaimsPrincipal CreatePrincipal(LocalUserCredentials user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.DisplayName),
            new(SecurityStampClaim, user.SecurityStamp),
            new(AuthorClaim, "true"),
        };
        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim(ClaimTypes.Email, user.Email));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static void AddBlogAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // Development runs over plain HTTP; production is always behind Caddy.
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.LoginPath = "/admin/login";
            options.LogoutPath = "/admin/logout";
            options.AccessDeniedPath = "/admin/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;

            // The cookie carries a security stamp: disabling the account or
            // changing its password invalidates every open session at once.
            options.Events.OnValidatePrincipal = async context =>
            {
                var principal = context.Principal;
                string? rawId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                string? stamp = principal?.FindFirstValue(SecurityStampClaim);
                if (!int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                    || string.IsNullOrEmpty(stamp))
                {
                    await RejectAsync(context);
                    return;
                }

                var store = context.HttpContext.RequestServices.GetRequiredService<LocalUserStore>();
                try
                {
                    var session = await store.FindSessionAsync(id, context.HttpContext.RequestAborted);
                    if (session is null
                        || !session.IsEnabled
                        || !string.Equals(session.SecurityStamp, stamp, StringComparison.Ordinal))
                    {
                        await RejectAsync(context);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("MainSite.Blog.BlogAuth");
                    logger.LogWarning(ex, "Could not validate the author session; signing out.");
                    await RejectAsync(context);
                }
            };
        });
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
