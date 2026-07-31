namespace MainSite;

// Il sito personale (matteofabbri.org). Ospita anche i PROGETTI: oggi il proxy dei
// download di Sperone (DownloadController), domani il resto di quello che era su
// maddalena.me.
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Telemetria, health check e service discovery condivisi della soluzione.
        builder.AddServiceDefaults();

        // Gli endpoint dei progetti sono controller con attribute routing (/api/*,
        // /download/*): niente view lato server.
        builder.Services.AddControllers();

        // DownloadController (proxy dei download di Sperone). Due client GitHub distinti:
        //  - "github-releases": scarica gli asset binari via l'URL stabile /releases/latest/download/…
        //    (niente API, niente rate limit). Timeout largo: i pacchetti pesano ~60-100 MB.
        //  - "github-api": legge l'API release per versione e dimensioni (serve l'header Accept di GitHub).
        // Entrambi mandano uno User-Agent, che GitHub pretende; i redirect verso il CDN li seguono da soli.
        builder.Services.AddHttpClient("github-releases", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("matteofabbri.org-sperone-download");
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        builder.Services.AddHttpClient("github-api", client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("matteofabbri.org-sperone-download");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });

        // Cache in memoria: l'elenco dei download tiene l'ultima release di GitHub per 15 minuti,
        // cosi' non si chiama l'API a ogni visita (che ha un rate limit).
        builder.Services.AddMemoryCache();

        var app = builder.Build();

        // Serve i file statici da wwwroot (css, js, immagini, ...).
        app.UseStaticFiles();

        app.MapControllers();          // /api/download/sperone + /download/sperone/{platform}

        app.MapGet("/", () => "hello world");

        app.MapDefaultEndpoints();

        app.Run();
    }
}
