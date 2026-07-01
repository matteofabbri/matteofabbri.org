namespace TheQult;

// L'API di theqult.org. Caddy le inoltra tutto il dominio (tranne /mail, che
// va alla webmail di mailcow). Per ora un endpoint d'esempio: qui crescera' l'API.
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Telemetria, health check e service discovery condivisi della soluzione.
        builder.AddServiceDefaults();

        var app = builder.Build();

        app.MapGet("/", () => new { name = "TheQult", status = "ok" });

        app.MapDefaultEndpoints();

        app.Run();
    }
}
