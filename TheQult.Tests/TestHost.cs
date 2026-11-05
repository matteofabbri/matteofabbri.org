using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TheQult.Data;
using TheQult.Services;

namespace TheQult.Tests;

// Fabbrica condivisa dei test: uno store Sperone vero, in una cartella temp
// per singolo test (ogni test vede un database pulito e lo smaltisce).
internal static class TestHost
{
    public static TheQultStore CreateStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "theqult-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        return new TheQultStore(
            Options.Create(new TheQultOptions { DataPath = dir }),
            new FakeEnvironment(dir),
            NullLoggerFactory.Instance,
            TimeProvider.System);
    }

    public static CommunityService CreateCommunity(TheQultStore store) =>
        new(store, TimeProvider.System);

    // Solo quello che lo store usa: ContentRootPath (per i percorsi relativi)
    // e i provider default.
    private sealed class FakeEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "TheQult.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public static CommunityService.JoinInput Join(
        string email = "matteo@example.com",
        string password = "couch-and-coffee",
        string displayName = "Matteo",
        string city = "Rome",
        string country = "IT",
        string hosting = Member.HostingHost,
        int maxGuests = 2,
        string languages = "Italiano, English",
        string about = "I have a couch and a coffee machine.") =>
        new(email, password, displayName, city, country, hosting, maxGuests, languages, about);
}
