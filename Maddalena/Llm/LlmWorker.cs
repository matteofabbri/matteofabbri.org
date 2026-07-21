using System.Net;
using System.Text;
using System.Threading.Tasks.Dataflow;
using Abot2.Crawler;
using Abot2.Poco;
using Microsoft.Extensions.Options;
using PredictiveCoding.Data.ArXiv;
using PredictiveCoding.Data.HackerNews;
using PredictiveCoding.Data.Web;
using PredictiveCoding.Data.Wikipedia;
using PredictiveCoding.LLM;

namespace Maddalena.Llm;

// Il ciclo di vita dell'LLM sta tutto qui, fuori dal percorso delle richieste:
//   1. riprende lo stato dalla cartella su disco (in prod il volume docker
//      maddalena-llm) o crea un modello nuovo al primo avvio;
//   2. pesca il corpus dalle datasources di PredictiveCoding.Data
//      (Wikipedia, Hacker News, arXiv, RSS — ognuna opzionale e tollerata al guasto);
//   3. lo assorbe con un giro di training e risalva lo stato;
//   4. marca il servizio Ready: da qui in poi /api/llm risponde;
//   5. e poi VIVE: il crawler Abot gira all'infinito (coda su Postgres), ogni
//      pagina buona diventa una lezione, e un timer scrive lo stato su disco
//      ogni SaveEveryMinutes se nel frattempo il modello ha imparato.
// Ogni fonte che fallisce e' un warning, non un errore: il sito non deve
// dipendere dalla salute del web altrui per partire.
public sealed partial class LlmWorker(
    LlmService service,
    LlmCrawlStore store,
    IOptions<LlmOptions> options,
    ILogger<LlmWorker> logger) : BackgroundService
{
    // La catena di montaggio che porta una pagina crawlata dentro al modello.
    // Due stazioni con ritmi opposti, ed e' tutto il punto:
    //
    //   ESTRAI (HTML -> prosa)  — parallela: e' lavoro di CPU indipendente,
    //     una pagina non sa niente dell'altra. Prima girava dentro l'handler
    //     dell'evento di Abot, cioe' SUL THREAD DEL CRAWLER: fino a 1 MB di
    //     parsing AngleSharp che rallentava il crawl invece del modello.
    //
    //   IMPARA (prosa -> pesi)  — rigorosamente UNA alla volta: lo scrittore
    //     e' uno solo, per la ragione scritta in LlmService (i gradienti
    //     vivono dentro i pesi). Qui l'invariante e' dichiarato, non sperato:
    //     MaxDegreeOfParallelism = 1.
    //
    // Ogni stazione ha la sua capienza: a valle pieno, il pezzo resta a monte,
    // e a monte pieno la pagina si butta. Il web non finisce, non serve
    // conservarle tutte — meglio perderne una che tenere ferma la catena.
    private ITargetBlock<RawPage>? _pipeline;

    // Il corpus che si sedimenta: append-only, non lo cancella nessuno. E' la
    // memoria lunga del crawl — il modello impara e dimentica, la cartella no.
    private ShardedCorpusWriter? _corpus;

    private readonly record struct RawPage(string Url, string Html);

    // La pagina ridotta ai suoi BLOCCHI di contenuto (un paragrafo, un titolo,
    // una voce di lista per elemento). Tenerli separati serve due volte: il
    // corpus li scrive una riga ciascuno — e righe uguali ripetute su mille
    // pagine (menu, banner cookie, footer) si deduplicano da sole in lettura —
    // mentre l'apprendimento li ricuce in un testo solo.
    private readonly record struct Lesson(string Url, IReadOnlyList<string> Blocks);

    // AngleSharp: un parser per chiamata (com'era prima), non uno condiviso —
    // il TransformBlock estrae in parallelo e la thread-safety di HtmlParser
    // non e' garantita. L'HttpClient invece si condivide: HtmlSource ne
    // creerebbe uno per istanza, e uno per pagina brucerebbe i socket. Qui non
    // viene mai usato per fetchare — scarica il crawler, noi solo parsiamo.
    private static readonly HttpClient ParserHttp = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService esegue ExecuteAsync in linea con l'avvio dell'host
        // fino al primo await: lo cediamo subito, il sito non aspetta l'LLM.
        await Task.Yield();

        var o = options.Value;

        try
        {
            // Un crash a meta' salvataggio puo' lasciare una .tmp orfana: via.
            string staleTmp = service.StateDir + ".tmp";
            if (Directory.Exists(staleTmp)) Directory.Delete(staleTmp, recursive: true);

            if (!TryResume())
            {
                var llm = PredictiveLlm.CreatePc(o.Dim, o.Blocks, o.Heads, o.FfDim, o.MaxSeq, o.Seed);
                service.Adopt(llm, resumed: false, lastSavedAt: null);
                logger.LogInformation("LLM nuovo: {Parameters:N0} parametri (dim {Dim}, {Blocks} blocchi, predictive-coding/iPC)",
                    llm.ParameterCount, o.Dim, o.Blocks);
            }

            // Giro di training BATCH sul corpus-seme all'avvio. Si puo' spegnere
            // (Llm__StartupCorpusTraining=false, default in dev) per un test in
            // PC puro: il modello parte vergine e impara SOLO dallo stream online
            // (crawler/teach) con l'ancora ogni PcAnchorEvery step.
            if (o.StartupCorpusTraining)
            {
                try
                {
                    var (corpus, summary) = LoadSeedCorpus();
                    // Sotto le due finestre di training non c'e' niente da imparare.
                    if (Encoding.UTF8.GetByteCount(corpus) >= 2048)
                    {
                        logger.LogInformation("LLM corpus dalle datasources: {Summary}", summary);
                        await service.TrainAsync(corpus, summary, stoppingToken);
                    }
                    else
                    {
                        logger.LogWarning("LLM corpus troppo piccolo ({Bytes} byte): training di avvio saltato",
                            Encoding.UTF8.GetByteCount(corpus));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;   // shutdown durante fetch/training: si riprende al prossimo avvio
                }
                catch (Exception ex)
                {
                    // Modello gia' in mano: un training di avvio fallito non spegne l'API.
                    logger.LogError(ex, "LLM: training di avvio fallito, il modello parte com'e'");
                }
            }

            // Pubblica la copia congelata dei pesi e apre l'API: da qui le
            // richieste leggono quella, senza incrociare il crawler.
            service.MarkReady();
            logger.LogInformation("LLM pronto su /api/llm");

            // La vita adulta del worker: crawl infinito, lezioni, salvataggi.
            var loops = new List<Task> { SaveLoopAsync(stoppingToken) };
            Task? lessons = null;
            if (o.CrawlerEnabled && o.CrawlerSeeds.Length > 0)
            {
                _corpus = OpenCorpus(o);
                (_pipeline, lessons) = BuildLearningPipeline(o, stoppingToken);
                loops.Add(CrawlLoopAsync(o, stoppingToken));
            }
            await Task.WhenAll(loops);

            // Il crawl e' finito (shutdown): chiudi l'imbocco e lascia che la
            // catena finisca i pezzi che ha gia' in mano.
            if (lessons is not null) await DrainPipelineAsync(lessons);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: si esce e basta, il salvataggio finale sta qui sotto.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLM: avvio fallito");
            service.MarkFailed(ex.Message);
        }

        _corpus?.Dispose();   // chiude lo shard aperto: il testo gia' scritto resta comunque

        // Ultimo salvataggio alla buona: se il timer non e' arrivato in tempo,
        // le lezioni del crawler non si perdono allo shutdown.
        try
        {
            if (await service.SaveIfDirtyAsync(CancellationToken.None))
                logger.LogInformation("LLM: stato salvato allo shutdown");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM: salvataggio allo shutdown fallito");
        }
    }

    // ── Il crawler (Abot, coda su Postgres) ──────────────────────────────────

    // Il crawl infinito: semina, gira, e quando la frontiera si prosciuga
    // ricicla le pagine fatte e riparte dai seed (i siti cambiano nel tempo).
    private async Task CrawlLoopAsync(LlmOptions o, CancellationToken ct)
    {
        try
        {
            await store.EnsureTableAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                if (await store.PendingCountAsync(ct) == 0)
                    await store.RecycleAsync(ct);
                foreach (string seed in o.CrawlerSeeds)
                    await store.EnqueueSeedAsync(seed, ct);

                using var crawler = BuildCrawler(o);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var result = await crawler.CrawlAsync(new Uri(o.CrawlerSeeds[0]), linked);
                logger.LogInformation("LLM crawler: giro concluso, {Pages} pagine visitate{Error}",
                    result.CrawlContext.CrawledCount,
                    result.ErrorException is null ? "" : $" (errore: {result.ErrorException.Message})");

                await Task.Delay(TimeSpan.FromSeconds(60), ct);   // respiro tra un giro e l'altro
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLM crawler: fermo per errore");
        }
    }

    private PoliteWebCrawler BuildCrawler(LlmOptions o)
    {
        var config = new CrawlConfiguration
        {
            // Nessun tetto di pagine o di tempo: l'unico limite e' la profondita'.
            MaxPagesToCrawl = 0,
            MaxPagesToCrawlPerDomain = 0,
            CrawlTimeoutSeconds = 0,
            MaxCrawlDepth = o.CrawlMaxDepth,
            MaxLinksPerPage = o.CrawlMaxLinksPerPage,
            MaxPageSizeInBytes = o.CrawlMaxPageBytes,
            // Il web intero, non solo i seed: i link esterni si seguono.
            IsExternalPageCrawlingEnabled = true,
            IsExternalPageLinksCrawlingEnabled = true,
            // Buona educazione: robots.txt, pochi thread, pausa per dominio, nome chiaro.
            IsRespectRobotsDotTextEnabled = true,
            RobotsDotTextUserAgentString = o.CrawlUserAgent,
            UserAgentString = o.CrawlUserAgent,
            MaxConcurrentThreads = o.CrawlThreads,
            MinCrawlDelayPerDomainMilliSeconds = o.CrawlDelayMs,
            HttpRequestTimeoutInSeconds = 15,
        };

        var crawler = new PoliteWebCrawler(
            config, null, null, new PostgresCrawlScheduler(store), null, null, null, null, null);
        crawler.PageCrawlCompleted += OnPageCrawled;
        return crawler;
    }

    // Dal thread del crawler alla catena: qui NIENTE lavoro pesante, solo la
    // consegna. Post e non SendAsync: a catena piena la pagina si butta invece
    // di trattenere il thread che sta crawlando.
    private void OnPageCrawled(object? sender, PageCrawlCompletedArgs e)
    {
        var pipeline = _pipeline;
        if (pipeline is null) return;

        var page = e.CrawledPage;
        if (page.HttpResponseMessage is not { IsSuccessStatusCode: true }) return;
        string html = page.Content?.Text ?? "";
        if (html.Length == 0) return;

        pipeline.Post(new RawPage(page.Uri.AbsoluteUri, html));
    }

    // ── La catena di montaggio ───────────────────────────────────────────────

    private (ITargetBlock<RawPage> Head, Task Completion) BuildLearningPipeline(LlmOptions o, CancellationToken ct)
    {
        // Stazione 1 — ESTRAI: CPU pura e indipendente per pagina, quindi in
        // parallelo. EnsureOrdered spento: l'ordine delle pagine non significa
        // niente, e senza vincolo una pagina lenta non trattiene le altre.
        var extract = new TransformBlock<RawPage, Lesson>(
            page => new Lesson(page.Url, ExtractBlocks(page.Html, o)),
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                BoundedCapacity = 32,
                EnsureOrdered = false,
                CancellationToken = ct,
            });

        // Stazione 3 — IMPARA: una alla volta, sempre. Non e' una scelta di
        // prestazioni ma di correttezza (vedi LlmService: un solo scrittore).
        var learn = new ActionBlock<Lesson>(
            async lesson =>
            {
                try
                {
                    // I blocchi si ricuciono con un a capo: il confine tra un
                    // paragrafo e il successivo e' informazione, e col
                    // tokenizer a byte costa un token.
                    string text = string.Join("\n", lesson.Blocks);
                    if (text.Length > o.CrawlLearnMaxChars) text = text[..o.CrawlLearnMaxChars];

                    await service.LearnFromPageAsync(text, ct);
                    logger.LogDebug("LLM: assorbita {Url} ({Chars} caratteri in {Blocks} blocchi)",
                        lesson.Url, text.Length, lesson.Blocks.Count);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "LLM: pagina {Url} non assorbita", lesson.Url);
                }
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 1,
                BoundedCapacity = 8,
                CancellationToken = ct,
            });

        // Le impronte delle righe gia' insegnate. Vive qui dentro e non ha
        // bisogno di lock: la stazione che la usa e' a parallelismo 1.
        var seen = new HashSet<long>();

        // Stazione 2 — SEDIMENTA: scrive la pagina nel corpus e POI la offre al
        // modello. L'ordine e' tutto il punto.
        //
        // Scrivere su disco costa millisecondi, imparare costa minuti. Se le
        // due cose stessero nella stessa stazione — o anche solo legate dalla
        // contropressione — il corpus crescerebbe al ritmo del modello, cioe'
        // si perderebbe la stragrande maggioranza di quel che il crawler pesca.
        // Invece qui il passaggio a valle e' un POST, non un link: se "impara"
        // e' occupato la lezione non viene presa e amen, ma il testo E' GIA'
        // NEL CORPUS. Il crawler non e' mai frenato dal modello — e la cartella
        // degli shard raccoglie tutto quello che passa.
        var sediment = new ActionBlock<Lesson>(
            lesson =>
            {
                try
                {
                    // Il corpus prende TUTTI i blocchi, senza il tetto che vale
                    // per l'apprendimento: su disco lo spazio costa poco, e un
                    // pretraining futuro vuole la pagina intera.
                    long written = _corpus?.Append(lesson.Blocks) ?? 0;
                    if (written > 0 && _corpus is not null)
                        service.ReportCorpus(_corpusBaseBytes + _corpus.BytesWritten, _corpus.DocumentsWritten);
                }
                catch (Exception ex)
                {
                    // Il disco pieno o in sola lettura non deve fermare
                    // l'apprendimento: si perde il corpus, non la lezione.
                    logger.LogWarning(ex, "LLM: pagina {Url} non sedimentata nel corpus", lesson.Url);
                }

                // Al modello vanno solo le righe che non ha gia' visto. Il
                // filtro sta QUI, dopo la scrittura su disco e prima del Post:
                // il corpus tiene tutto (la ripetizione e' un dato), il modello
                // riceve il meglio.
                var fresh = Fresh(lesson.Blocks, seen, o.CrawlDedupWindow);
                if (fresh.Count > 0)
                    learn.Post(lesson with { Blocks = fresh });   // false = il modello e' indietro: pazienza, il testo resta su disco
            },
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = 128 });

        // Le pagine senza prosa utile (menu, gallerie, errori) non entrano
        // nemmeno nel corpus: cadono nel NullTarget, il cestino di Dataflow —
        // senza, resterebbero nel buffer a ingorgare la stazione a monte.
        //
        // I DUE PREDICATI SONO COMPLEMENTARI APPOSTA. Un cestino senza
        // predicato accetterebbe qualunque cosa: quando la stazione a valle e'
        // piena rifiuta (temporaneamente) anche le pagine buone, e quelle
        // finirebbero dritte nel cestino invece di aspettare il loro turno —
        // si butterebbe proprio il materiale migliore, proprio quando la
        // catena e' impegnata. Col predicato negato il cestino prende solo gli
        // scarti veri, e una pagina buona resta in coda finche' c'e' posto (a
        // monte pieno, e' il Post del crawler a dire di no).
        extract.LinkTo(sediment, new DataflowLinkOptions { PropagateCompletion = true }, l => l.Blocks.Count > 0);
        extract.LinkTo(DataflowBlock.NullTarget<Lesson>(), l => l.Blocks.Count == 0);

        // La completion non si propaga da sola oltre un Post: la si passa a mano.
        _ = sediment.Completion.ContinueWith(_ => learn.Complete(), TaskScheduler.Default);

        return (extract, learn.Completion);
    }

    // Byte gia' presenti negli shard quando il processo e' partito: il writer
    // conta solo i propri, e lo status deve mostrare il totale accumulato.
    private long _corpusBaseBytes;

    private ShardedCorpusWriter? OpenCorpus(LlmOptions o)
    {
        if (!o.CorpusEnabled) return null;
        try
        {
            string dir = service.CorpusDir;   // sorella di StateDir: dentro il volume, mai dentro lo stato
            var writer = new ShardedCorpusWriter(dir, o.CorpusShardMaxBytes);
            _corpusBaseBytes = Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles("shard-*.txt").Sum(f => f.Length)
                : 0;
            service.ReportCorpus(_corpusBaseBytes, 0);
            logger.LogInformation("LLM corpus: shard in {Dir} ({Bytes:N0} byte gia' accumulati)", dir, _corpusBaseBytes);
            return writer;
        }
        catch (Exception ex)
        {
            // Senza corpus il sito funziona lo stesso: si perde solo la
            // sedimentazione, non l'apprendimento.
            logger.LogError(ex, "LLM corpus: cartella non apribile, il crawl imparera' senza sedimentare");
            return null;
        }
    }

    // Chiude l'imbocco e da' alla catena qualche secondo per finire quel che ha
    // in mano. Non di piu': LearnFromPageAsync si ferma da sola tra un boccone
    // e l'altro, e quel che non entra si ricrawla al prossimo avvio.
    private async Task DrainPipelineAsync(Task completion)
    {
        _pipeline?.Complete();
        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogDebug("LLM: catena delle lezioni chiusa senza svuotarsi (shutdown)");
        }
    }

    // I blocchi che il modello non ha ancora visto, nell'ordine originale.
    //
    // L'impronta e' un intero, non la riga: tenere un milione di stringhe
    // costerebbe centinaia di MB, un milione di long ne costa otto. Combina
    // hash e lunghezza — due righe diverse collidono solo se coincidono su
    // entrambi, e una collisione costa una riga buona saltata ogni tanto.
    // Al riavvio la memoria riparte vuota (GetHashCode e' randomizzato per
    // processo): il boilerplate rientra una volta e poi si richiude da solo.
    private static IReadOnlyList<string> Fresh(IReadOnlyList<string> blocks, HashSet<long> seen, int window)
    {
        if (window <= 0) return blocks;
        if (seen.Count >= window) seen.Clear();   // pieno: si riparte, non si cresce all'infinito

        var fresh = new List<string>(blocks.Count);
        foreach (string b in blocks)
        {
            long fingerprint = ((long)b.GetHashCode() << 32) ^ (uint)b.Length;
            if (seen.Add(fingerprint)) fresh.Add(b);
        }
        return fresh;
    }

    // HTML -> i blocchi di prosa che vale la pena imparare (vuoto = pagina da
    // scartare).
    //
    // Qui c'era `Body.TextContent`, e non era il testo della pagina: e' il
    // testo di OGNI nodo, script compresi. Su un articolo di giornale tipo,
    // dei 1.313 caratteri che ne uscivano solo 305 erano l'articolo — il resto
    // era il tag di Google Analytics, il JSON-LD di schema.org, il banner
    // cookie, il menu e la partita IVA del footer. Il modello stava imparando
    // JavaScript. E il guaio peggiore non e' lo spreco: quella roba e'
    // IDENTICA su ogni pagina del sito, quindi la vede migliaia di volte ed e'
    // l'unica cosa che imparerebbe davvero bene.
    //
    // ParseBlocks (PredictiveCoding.Data, ROADMAP-Scale C1/C2) toglie
    // script/style/nav/header/footer/aside/form dal DOM e restituisce un blocco
    // per paragrafo, titolo, voce di lista.
    private static IReadOnlyList<string> ExtractBlocks(string html, LlmOptions o)
    {
        try
        {
            using var source = new HtmlSource(ParserHttp);
            var blocks = source.ParseBlocks(html);

            int total = 0;
            foreach (string b in blocks) total += b.Length;
            return total < o.CrawlLearnMinChars ? [] : blocks;
        }
        catch
        {
            return [];
        }
    }

    // Il timer del salvataggio: ogni SaveEveryMinutes scrive lo stato su disco,
    // ma solo se dall'ultima scrittura il modello ha imparato qualcosa.
    private async Task SaveLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.SaveEveryMinutes)));
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (await service.SaveIfDirtyAsync(ct))
                        logger.LogInformation("LLM: stato salvato dal timer ({Pages} pagine assorbite dall'avvio)",
                            service.PagesLearned);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "LLM: salvataggio periodico fallito");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // Riprende il modello dalla cartella di stato, se c'e' e si fa leggere.
    // Uno stato non riutilizzabile (file corrotti / deploy interrotto a meta' Save,
    // pesi NaN, o taglia del modello cambiata in config) non blocca il sito:
    // finisce da parte in *.corrotto e si riparte da zero alla config attuale.
    private bool TryResume()
    {
        string stateDir = service.StateDir;
        string marker = Path.Combine(stateDir, "config.json");
        if (!File.Exists(marker)) return false;

        try
        {
            var llm = PredictiveLlm.Load(stateDir);
            // Un checkpoint con pesi NaN/Inf (training andato instabile) si carica
            // senza errori ma poi genera solo token 0 ('\0'): lo trattiamo come
            // corrotto cosi' il ramo sotto lo mette da parte e si riparte freschi.
            if (!llm.WeightsAreFinite())
                throw new InvalidDataException(
                    "checkpoint con pesi non finiti (NaN/Inf): stato avvelenato da un training instabile.");

            // Se hai cambiato la taglia del modello in config, i vecchi pesi non
            // entrano nella nuova architettura: mettiamo da parte lo stato e
            // ripartiamo freschi alla taglia nuova (stessa magia del guard NaN).
            var o = options.Value;
            var c = llm.Config;
            if (c.Dim != o.Dim || c.NumBlocks != o.Blocks || c.NumHeads != o.Heads ||
                c.FfDim != o.FfDim || c.MaxSeq != o.MaxSeq)
                throw new InvalidDataException(
                    $"architettura cambiata: stato salvato [dim {c.Dim}, blocks {c.NumBlocks}, heads {c.NumHeads}, ff {c.FfDim}, seq {c.MaxSeq}] " +
                    $"!= config [dim {o.Dim}, blocks {o.Blocks}, heads {o.Heads}, ff {o.FfDim}, seq {o.MaxSeq}]. Riparto fresco alla taglia nuova.");

            service.Adopt(llm, resumed: true, lastSavedAt: File.GetLastWriteTimeUtc(marker));
            logger.LogInformation("LLM ripreso dallo stato in {StateDir} (salvato il {SavedAt:u})",
                stateDir, File.GetLastWriteTimeUtc(marker));
            return true;
        }
        catch (Exception ex)
        {
            string aside = stateDir + ".corrotto";
            if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            Directory.Move(stateDir, aside);
            logger.LogWarning(ex, "LLM: stato in {StateDir} non riutilizzabile, messo da parte in {Aside}; si riparte da zero",
                stateDir, aside);
            return false;
        }
    }

    // ── Il corpus-seme (riassunti Wikipedia, embeddato nell'assembly) ─────────

    // Il training d'avvio parte da un corpus statico e CURATO: niente fetch di
    // rete all'avvio (fragile, fuori dal nostro controllo). Il testo vive in
    // Llm/SeedCorpus.txt, embeddato come risorsa (vedi Maddalena.csproj), cosi'
    // viaggia dentro l'immagine docker senza dipendere da percorsi su disco.
    private static (string Corpus, string Summary) LoadSeedCorpus()
    {
        var asm = typeof(LlmWorker).Assembly;
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("SeedCorpus.txt", StringComparison.Ordinal));
        if (name is null)
            return ("", "corpus-seme: risorsa SeedCorpus.txt non trovata");

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string corpus  = reader.ReadToEnd();
        int    docs    = corpus.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
        string summary = $"corpus-seme: {docs} documenti, {Encoding.UTF8.GetByteCount(corpus) / 1024} KB";
        return (corpus, summary);
    }
}
