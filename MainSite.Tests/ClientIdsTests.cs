using MainSite.RequestLogging;

namespace MainSite.Tests;

public sealed class ClientIdsTests
{
    [Fact]
    public void Lo_stesso_ip_e_lo_stesso_user_agent_danno_lo_stesso_id()
    {
        Assert.Equal(
            ClientIds.For("203.0.113.7", "Mozilla/5.0"),
            ClientIds.For("203.0.113.7", "Mozilla/5.0"));
    }

    [Fact]
    public void Ip_o_user_agent_diversi_danno_id_diversi()
    {
        Assert.NotEqual(
            ClientIds.For("203.0.113.7", "Mozilla/5.0"),
            ClientIds.For("203.0.113.8", "Mozilla/5.0"));
        Assert.NotEqual(
            ClientIds.For("203.0.113.7", "Mozilla/5.0"),
            ClientIds.For("203.0.113.7", "curl/8"));
    }

    [Fact]
    public void Senza_ip_e_senza_user_agent_non_c_e_id()
    {
        Assert.Null(ClientIds.For(null, null));
        Assert.Null(ClientIds.For("", " "));
    }
}
