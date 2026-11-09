namespace BotDetection.Client;

/// <summary>The two flags the site persists on a request log entry.</summary>
public sealed record BotVerdict(bool IsBot, bool IsMalicious, double Probability, string ModelVersion);
