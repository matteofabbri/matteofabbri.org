using System.Threading.Channels;
using Maddalena.Data;
using Microsoft.EntityFrameworkCore;

namespace Maddalena.Llm;

// Log dell'LLM su Postgres (tabella llm_log). La scrittura NON blocca l'LLM:
// gli eventi finiscono in un canale a capienza fissa e un loop in background li
// scarica su Postgres. L'LLM gira su un solo gate (una operazione per volta),
// quindi loggare in modo sincrono lo rallenterebbe: qui invece l'Enqueue e'
// immediato e, se il canale e' pieno, si butta il piu' vecchio (un log perso non
// deve mai fermare il modello). Tabella FUORI dal modello EF: la crea
// EnsureTableAsync con CREATE TABLE IF NOT EXISTS, come llm_crawl_queue.
public sealed class LlmLogStore(IServiceScopeFactory scopeFactory) : IHostedService
{
    private readonly Channel<LogItem> _channel = Channel.CreateBounded<LogItem>(
        new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    private readonly record struct LogItem(
        DateTimeOffset At, string Level, string Category, string? Event, string Message, string? Exception);

    /// <summary>Accoda un evento. Non blocca; a canale pieno scarta il piu' vecchio.</summary>
    public void Enqueue(string level, string category, string message, string? exception = null, string? evt = null)
        => _channel.Writer.TryWrite(new LogItem(DateTimeOffset.UtcNow, level, category, evt, message, exception));

    public Task StartAsync(CancellationToken _)
    {
        _loop = Task.Run(() => FlushLoopAsync(_stopping.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken _)
    {
        _stopping.Cancel();
        _channel.Writer.TryComplete();
        if (_loop is not null) { try { await _loop; } catch { /* shutdown */ } }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        // Il DB (Aspire) puo' non essere ancora su all'avvio: ritenta la creazione
        // della tabella finche' non riesce, poi scarica il canale.
        for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
        {
            try { await EnsureTableAsync(ct); break; }
            catch { try { await Task.Delay(2000, ct); } catch { return; } }
        }

        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        INSERT INTO llm_log (logged_at, level, category, evt, message, exception)
                        VALUES ({item.At}, {item.Level}, {item.Category}, {item.Event}, {item.Message}, {item.Exception})
                        """, ct);
                }
                catch { /* un log perso non deve mai far crashare il loop */ }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public async Task EnsureTableAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS llm_log (
                id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                logged_at timestamptz NOT NULL,
                level     text NOT NULL,
                category  text NOT NULL,
                evt       text,
                message   text NOT NULL,
                exception text
            );
            CREATE INDEX IF NOT EXISTS ix_llm_log_at ON llm_log (logged_at DESC);
            """, ct);
    }

    /// <summary>Gli ultimi <paramref name="limit"/> eventi, piu' recente per primo. Filtra per livello se dato.</summary>
    public async Task<IReadOnlyList<LlmLogRow>> GetRecentAsync(int limit, string? level, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        const string cols =
            """
            SELECT id AS "Id", logged_at AS "At", level AS "Level", category AS "Category",
                   evt AS "Event", message AS "Message", exception AS "Exception"
            FROM llm_log
            """;
        // Query condizionale: senza filtro niente parametro nullo (Npgsql non
        // inferisce il tipo di un null) -> nessun rischio a runtime.
        if (string.IsNullOrEmpty(level))
            return await db.Database.SqlQueryRaw<LlmLogRow>(
                cols + " ORDER BY logged_at DESC, id DESC LIMIT {0}", limit).ToListAsync(ct);

        return await db.Database.SqlQueryRaw<LlmLogRow>(
            cols + " WHERE level = {0} ORDER BY logged_at DESC, id DESC LIMIT {1}", level, limit).ToListAsync(ct);
    }
}

/// <summary>Una riga di log come letta da Postgres (ed esposta su /api/llm/logs).</summary>
public sealed class LlmLogRow
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Level { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Event { get; set; }
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
}
