namespace MainSite;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Telemetria, health check e service discovery condivisi della soluzione.
        builder.AddServiceDefaults();

        var app = builder.Build();

        // Serve i file statici da wwwroot (css, js, immagini, ...).
        app.UseStaticFiles();

        app.MapGet("/", () => "hello world");

        app.MapDefaultEndpoints();

        app.Run();
    }
}
