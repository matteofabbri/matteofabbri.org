using MainSite.RequestLogging;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class RequestLogQueueTests
{
    [Fact]
    public void Quando_la_coda_e_piena_scarta_le_nuove_righe_e_le_conta()
    {
        var queue = new RequestLogQueue(Options.Create(new RequestLogOptions
        {
            QueueCapacity = 16, // il minimo effettivo
        }));

        for (int i = 0; i < 16; i++)
            Assert.True(queue.TryWrite(Entry(i)));

        Assert.False(queue.TryWrite(Entry(16)));
        Assert.Equal(1, queue.Dropped);

        // Il writer legge e azzera il contatore per un solo avviso aggregato.
        Assert.Equal(1, queue.TakeDropped());
        Assert.Equal(0, queue.Dropped);
    }

    [Fact]
    public void Una_volta_completata_la_coda_non_accetta_altro()
    {
        var queue = new RequestLogQueue(Options.Create(new RequestLogOptions()));

        queue.Complete();

        Assert.False(queue.TryWrite(Entry(1)));
        Assert.Equal(0, queue.Dropped); // non è una coda "piena": è lo shutdown
    }

    private static RequestLogEntry Entry(int index) => new()
    {
        Id = index.ToString(),
        Timestamp = DateTimeOffset.UnixEpoch,
        Method = "GET",
        Scheme = "https",
        Host = "matteofabbri.org",
        Path = "/",
    };
}
