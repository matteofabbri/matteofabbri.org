using MainSite.RequestLogging;

namespace MainSite.Tests;

public sealed class RequestFlagStateTests
{
    [Fact]
    public void A_click_cycles_unknown_to_yes_to_no_and_back_to_unknown()
    {
        Assert.True(RequestFlagState.Next(null));
        Assert.False(RequestFlagState.Next(true));
        Assert.Null(RequestFlagState.Next(false));
    }

    [Fact]
    public void A_state_round_trips_through_the_form_value()
    {
        Assert.Equal("", RequestFlagState.Value(null));
        Assert.Equal("true", RequestFlagState.Value(true));
        Assert.Equal("false", RequestFlagState.Value(false));
    }

    [Fact]
    public void The_click_hint_names_the_state_the_click_produces()
    {
        Assert.Equal("Click to set yes", RequestFlagState.ClickHint(null));
        Assert.Equal("Click to set no", RequestFlagState.ClickHint(true));
        Assert.Equal("Click to reset to the model", RequestFlagState.ClickHint(false));
    }

    [Fact]
    public void The_badge_classes_are_the_ones_the_public_page_uses()
    {
        Assert.Equal("bot-unknown", RequestFlagState.BotBadgeClass(null));
        Assert.Equal("bot-automated", RequestFlagState.BotBadgeClass(true));
        Assert.Equal("bot-human", RequestFlagState.BotBadgeClass(false));

        Assert.Equal("bot-unknown", RequestFlagState.MaliciousBadgeClass(null));
        Assert.Equal("bot-malicious", RequestFlagState.MaliciousBadgeClass(true));
        Assert.Equal("bot-human", RequestFlagState.MaliciousBadgeClass(false));
    }
}
