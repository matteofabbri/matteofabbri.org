using Abot2.Core;
using Abot2.Poco;

namespace Maddalena.Llm;

// Lo scheduler di Abot con la coda su Postgres (LlmCrawlStore) invece che in
// memoria: la frontiera del crawl sopravvive ai riavvii e il giro riprende da
// dove era.
//
// ATTENZIONE a Clear(): Abot lo chiama quando il crawl viene FERMATO (stop,
// hard stop, cancellazione — cioe' anche a ogni shutdown del sito). Svuotare
// davvero la tabella butterebbe la coda persistente proprio nel momento in cui
// serve. Quindi Clear alza solo un flag in-memory: questo GIRO si ferma (Count
// = 0, GetNext = null e il loop di Abot conclude), il database resta intatto.
// Lo scheduler e' usa-e-getta: uno nuovo per ogni giro di crawl.
public sealed class PostgresCrawlScheduler(LlmCrawlStore store) : IScheduler
{
    private volatile bool _cleared;

    public int Count => _cleared ? 0 : store.PendingCount();

    public void Add(PageToCrawl page)
    {
        if (_cleared || page?.Uri is null) return;
        string url = page.Uri.AbsoluteUri;
        if (url.Length > 2000) return;   // URL chilometrici: non valgono una riga di coda
        store.AddPending(url, page.CrawlDepth, page.IsInternal);
    }

    public void Add(IEnumerable<PageToCrawl> pages)
    {
        foreach (var page in pages) Add(page);
    }

    public void AddKnownUri(Uri uri)
    {
        if (uri.AbsoluteUri.Length > 2000) return;
        store.MarkKnown(uri.AbsoluteUri);
    }

    public bool IsUriKnown(Uri uri) =>
        uri.AbsoluteUri.Length > 2000 || store.IsKnown(uri.AbsoluteUri);

    public PageToCrawl? GetNext()
    {
        if (_cleared) return null;
        var row = store.PopNext();
        return row is null
            ? null
            : new PageToCrawl(new Uri(row.Url)) { CrawlDepth = row.Depth, IsInternal = row.IsInternal };
    }

    public void Clear() => _cleared = true;

    public void Dispose()
    {
        // Niente da liberare: le connessioni vivono dentro le singole chiamate.
    }
}
