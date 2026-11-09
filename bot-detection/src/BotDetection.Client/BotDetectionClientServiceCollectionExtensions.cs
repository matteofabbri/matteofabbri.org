using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotDetection.Client;

public static class BotDetectionClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gRPC classifier and the rate tracker. The AppHost injects
    /// BotDetection__GrpcUrl; the defaults keep the site working (flags left
    /// null) when the model is not deployed.
    /// </summary>
    public static IServiceCollection AddBotDetectionClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<BotDetectionClientOptions>()
            .Bind(configuration.GetSection(BotDetectionClientOptions.SectionName));

        services.AddSingleton<BotClassifier>();
        services.AddSingleton<ClientRateTracker>();

        return services;
    }
}
