using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace MainSite.RequestLogging;

// Il middleware scrive qui, il writer legge. È un'interfaccia per poter
// provare il middleware senza toccare il database Sperone.
public interface IRequestLogSink
{
    bool TryWrite(RequestLogEntry entry);
}

// Coda limitata in memoria: la richiesta non aspetta mai Sperone. Se il
// database è lento o irraggiungibile e la coda si riempie, le nuove righe si
// scartano (e il writer lo dice nei log) invece di far crescere la memoria
// o rallentare il sito.
public sealed class RequestLogQueue : IRequestLogSink
{
    private readonly Channel<RequestLogEntry> _channel;
    private long _dropped;
    private volatile bool _completed;

    public RequestLogQueue(IOptions<RequestLogOptions> options)
    {
        _channel = Channel.CreateBounded<RequestLogEntry>(new BoundedChannelOptions(options.Value.EffectiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public ChannelReader<RequestLogEntry> Reader => _channel.Reader;

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryWrite(RequestLogEntry entry)
    {
        if (_completed)
            return false;

        if (_channel.Writer.TryWrite(entry))
            return true;

        Interlocked.Increment(ref _dropped);
        return false;
    }

    // Solo lo shutdown: da qui in poi il writer drena quello che c'è e termina.
    public void Complete()
    {
        _completed = true;
        _channel.Writer.TryComplete();
    }

    // Legge e azzera il contatore: il writer lo usa per un avviso aggregato,
    // non per una riga di log per ogni richiesta scartata.
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);
}
