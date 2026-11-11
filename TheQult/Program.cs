using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using TheQult.Data;
using TheQult.Services;

namespace TheQult;

// The theqult.org site: the hospitality community with no subscriptions.
// Caddy forwards the whole domain here; there is no webmail prefix any more.
// The old sample endpoint became this site; the health endpoint stays at
// /api/status for the monitoring scripts.
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Telemetria, health check e service discovery condivisi della soluzione.
        builder.AddServiceDefaults();

        // --- Dati e regole ------------------------------------------------------
        builder.Services.Configure<TheQultOptions>(builder.Configuration.GetSection(TheQultOptions.Section));

        // Il percorso dei dati serve subito anche alle chiavi DataProtection:
        // nel volume (in produzione /data/theqult) sopravvivono al riavvio del
        // container e la sessione di ognuno non cade a ogni deploy.
        var thequltOptions = builder.Configuration
            .GetSection(TheQultOptions.Section)
            .Get<TheQultOptions>() ?? new();
        string dataPath = Path.IsPathRooted(thequltOptions.DataPath)
            ? thequltOptions.DataPath
            : Path.Combine(builder.Environment.ContentRootPath, thequltOptions.DataPath);
        Directory.CreateDirectory(dataPath);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<TheQultStore>();
        builder.Services.AddScoped<CommunityService>();

        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));

        // --- Accesso: cookie di sessione ----------------------------------------
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
                options.AccessDeniedPath = "/404";
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;
                options.Cookie.Name = "theqult.session";
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Dietro il nostro Caddy il protocollo arriva in X-Forwarded-Proto:
                // il cookie è sempre Secure, tranne quando gira in puro HTTP locale.
                options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
            });
        builder.Services.AddAuthorization();
        builder.Services.AddRazorPages();

        // Dietro il NOSTRO Caddy: fidati degli X-Forwarded-* (serve al limite
        // per IP, che altrimenti vedrebbe l'indirizzo del proxy).
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        // Iscrizione e login non fanno nulla di costoso, ma sono esattamente
        // il punto dove qualcuno prova a martellare il server: tetto per IP.
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(10),
                        QueueLimit = 0,
                    }));
        });

        var app = builder.Build();

        app.UseForwardedHeaders();

        // 404 vera (status 404) su una pagina comoda da leggere.
        app.UseStatusCodePagesWithReExecute("/404");

        app.UseStaticFiles();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapRazorPages();

        // Stato macchina per script e sorveglianza: era il "/" di una volta.
        app.MapGet("/api/status", () => new { name = "TheQult", status = "ok" });

        app.MapDefaultEndpoints();

        app.Run();
    }
}
