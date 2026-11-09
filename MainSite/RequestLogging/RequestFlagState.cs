namespace MainSite.RequestLogging;

/// <summary>
/// The three states of the two flags a request log row carries: unknown ("—",
/// which leaves the row to the model), yes and no. The admin page turns each
/// flag into a single button, so a click moves it to the next state and closes
/// the cycle back to "—", where the model decides again.
/// </summary>
public static class RequestFlagState
{
    /// <summary>The state a click produces: "—" → yes → no → "—".</summary>
    public static bool? Next(bool? value) => value switch
    {
        null => true,
        true => false,
        _ => null,
    };

    /// <summary>The form value of a state: "true", "false" or empty for "—".</summary>
    public static string Value(bool? value) => value switch
    {
        true => "true",
        false => "false",
        _ => "",
    };

    /// <summary>How a state is written next to its flag name.</summary>
    public static string Label(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        _ => "—",
    };

    /// <summary>The tooltip naming the state the click sets, derived from <see cref="Next"/>.</summary>
    public static string ClickHint(bool? value) => Next(value) switch
    {
        true => "Click to set yes",
        false => "Click to set no",
        _ => "Click to reset to the model",
    };

    /// <summary>The bot badge class, the same one the public requests page uses.</summary>
    public static string BotBadgeClass(bool? value) => value switch
    {
        true => "bot-automated",
        false => "bot-human",
        _ => "bot-unknown",
    };

    /// <summary>The malicious badge class, the same one the public requests page uses.</summary>
    public static string MaliciousBadgeClass(bool? value) => value switch
    {
        true => "bot-malicious",
        false => "bot-human",
        _ => "bot-unknown",
    };
}
