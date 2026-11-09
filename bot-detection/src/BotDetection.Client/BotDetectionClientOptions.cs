using System.ComponentModel.DataAnnotations;

namespace BotDetection.Client;

public sealed class BotDetectionClientOptions
{
    /// <summary>Configuration section shared with the AppHost (BotDetection__*).</summary>
    public const string SectionName = "BotDetection";

    public bool Enabled { get; set; } = true;

    /// <summary>gRPC endpoint of the Python service; the AppHost injects BotDetection__GrpcUrl.</summary>
    public string GrpcUrl { get; set; } = "http://localhost:50051";

    /// <summary>Per-batch budget. The writer runs in the background, so this is not request latency.</summary>
    [Range(50, 30_000)]
    public int TimeoutMilliseconds { get; set; } = 1_000;
}
