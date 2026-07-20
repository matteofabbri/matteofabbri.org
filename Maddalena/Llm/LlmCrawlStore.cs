using Maddalena.Data;
using Microsoft.EntityFrameworkCore;

namespace Maddalena.Llm;

// La coda del crawler su Postgres: frontiera (pagine da visitare, status 0) e
// pagine gia' note (status 1) in un'unica tabella. Sopravvive ai riavvii: il
// crawl riprende da dove era. Tabella FUORI dal modello EF (EnsureCreated non
// crea tabelle nuove su un DB gia' esistente): la crea EnsureTableAsync.
//
// I metodi SINCRONI servono a PostgresCrawlScheduler: l'IScheduler di Abot e'
// un'interfaccia sincrona chiamata dai thread del crawler. Ogni chiamata apre
// il suo scope (DbContext non e' thread-safe, il crawler ha piu' thread).
// REGOLA D'ORO (dal sorgente di Abot): AddKnownUri arriva per ogni link DOPO
// l'eventuale Add — quindi qui si INSERISCE solo se la riga non esiste, e non
// si cambia MAI lo status di una riga esistente (una pending resterebbe
// pending, una fatta resta fatta).
public sealed class LlmCrawlStore(IServiceScopeFactory scopeFactory)
{
    public async Task EnsureTableAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS llm_crawl_queue (
                url         text PRIMARY KEY,
                depth       int NOT NULL,
                is_internal boolean NOT NULL,
                status      smallint NOT NULL,
                added_at    timestamptz NOT NULL,
                crawled_at  timestamptz
            );
            CREATE INDEX IF NOT EXISTS ix_llm_crawl_queue_pending
                ON llm_crawl_queue (added_at) WHERE status = 0;
            """, ct);
    }

    // ── Lato Abot (sincrono, thread del crawler) ─────────────────────────────

    public bool IsKnown(string url)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.Database
            .SqlQueryRaw<bool>("""SELECT EXISTS(SELECT 1 FROM llm_crawl_queue WHERE url = {0}) AS "Value" """, url)
            .AsEnumerable().First();
    }

    public void AddPending(string url, int depth, bool isInternal)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(
            """
            INSERT INTO llm_crawl_queue (url, depth, is_internal, status, added_at)
            VALUES ({0}, {1}, {2}, 0, now()) ON CONFLICT (url) DO NOTHING
            """, url, depth, isInternal);
    }

    public void MarkKnown(string url)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(
            """
            INSERT INTO llm_crawl_queue (url, depth, is_internal, status, added_at, crawled_at)
            VALUES ({0}, 0, false, 1, now(), now()) ON CONFLICT (url) DO NOTHING
            """, url);
    }

    /// <summary>Estrae la prossima pagina da visitare marcandola gia' fatta (un crash a meta' perde solo quella).</summary>
    public CrawlRow? PopNext()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.Database
            .SqlQueryRaw<CrawlRow>(
                """
                UPDATE llm_crawl_queue SET status = 1, crawled_at = now()
                WHERE url = (SELECT url FROM llm_crawl_queue WHERE status = 0 ORDER BY added_at, url LIMIT 1)
                RETURNING url AS "Url", depth AS "Depth", is_internal AS "IsInternal"
                """)
            .AsEnumerable().FirstOrDefault();
    }

    public int PendingCount()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.Database
            .SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM llm_crawl_queue WHERE status = 0""")
            .AsEnumerable().First();
    }

    // ── Lato worker e status (asincrono) ─────────────────────────────────────

    public async Task EnqueueSeedAsync(string url, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO llm_crawl_queue (url, depth, is_internal, status, added_at)
            VALUES ({url}, 0, true, 0, now()) ON CONFLICT (url) DO NOTHING
            """, ct);
    }

    public async Task<long> PendingCountAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Database
            .SqlQuery<long>($"""SELECT count(*) AS "Value" FROM llm_crawl_queue WHERE status = 0""")
            .FirstAsync(ct);
    }

    public async Task<long> KnownCountAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Database
            .SqlQuery<long>($"""SELECT count(*) AS "Value" FROM llm_crawl_queue""")
            .FirstAsync(ct);
    }

    /// <summary>Coda prosciugata: dimentica le pagine fatte, cosi' il prossimo giro ricrawla dai seed.</summary>
    public async Task RecycleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlAsync($"DELETE FROM llm_crawl_queue WHERE status = 1", ct);
    }
}

/// <summary>Una pagina della frontiera com'e' letta da Postgres.</summary>
public sealed class CrawlRow
{
    public string Url { get; set; } = "";
    public int Depth { get; set; }
    public bool IsInternal { get; set; }
}
