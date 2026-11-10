namespace BotDetection.Client;

/// <summary>The two flags the site persists on a request log entry.</summary>
public sealed record BotVerdict(bool IsBot, bool IsMalicious, double Probability, string ModelVersion);

/// <summary>
/// The model's answer to a manual client label: how many stored observations
/// of the client were relabelled, whether the manual verdict is active for its
/// next requests, and whether a retraining run was requested.
/// </summary>
public sealed record BotFeedbackResult(
    int ObservationsUpdated,
    bool ManualLabelActive,
    bool RetrainRequested);
