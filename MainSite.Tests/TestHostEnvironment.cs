using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MainSite.Tests;

// Un IHostEnvironment minimo: alla store serve solo la content root.
internal sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "MainSite.Tests";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
