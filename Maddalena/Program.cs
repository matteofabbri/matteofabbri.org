namespace Maddalena;

// Il sito maddalena.me. Per ora è solo un segnaposto: il vecchio frontend
// React e il submodule maddelena-FE sono stati rimossi. Il servizio resta
// nello stack (dominio, Caddy e DNS già configurati) finché non arriverà il
// nuovo contenuto.
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddServiceDefaults();

        var app = builder.Build();

        app.MapGet("/", () => "hello world");

        app.MapDefaultEndpoints();

        app.Run();
    }
}
