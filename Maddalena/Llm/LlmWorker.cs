using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Abot2.Crawler;
using Abot2.Poco;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using PredictiveCoding.Data.ArXiv;
using PredictiveCoding.Data.HackerNews;
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
    // Le pagine crawlate in attesa di diventare lezioni. Il crawler produce
    // piu' veloce di quanto il modello impari: canale a capienza fissa, e a
    // canale pieno le pagine nuove si buttano — il web non finisce, non serve
    // conservarle tutte.
    private readonly Channel<(string Url, string Text)> _pages =
        Channel.CreateBounded<(string Url, string Text)>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
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

            service.MarkReady();
            logger.LogInformation("LLM pronto su /api/llm");

            // La vita adulta del worker: crawl infinito, lezioni, salvataggi.
            var loops = new List<Task> { SaveLoopAsync(stoppingToken) };
            if (o.CrawlerEnabled && o.CrawlerSeeds.Length > 0)
            {
                loops.Add(CrawlLoopAsync(o, stoppingToken));
                loops.Add(LearnLoopAsync(stoppingToken));
            }
            await Task.WhenAll(loops);
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

    // Dal thread del crawler alla coda delle lezioni: qui solo estrazione del
    // testo; l'apprendimento (lento, sotto semaforo) sta nel LearnLoop.
    private void OnPageCrawled(object? sender, PageCrawlCompletedArgs e)
    {
        var page = e.CrawledPage;
        if (page.HttpResponseMessage is not { IsSuccessStatusCode: true }) return;
        string html = page.Content?.Text ?? "";
        if (html.Length == 0) return;

        var o = options.Value;
        string text = ExtractText(html);
        if (text.Length < o.CrawlLearnMinChars) return;
        if (text.Length > o.CrawlLearnMaxChars) text = text[..o.CrawlLearnMaxChars];
        _pages.Writer.TryWrite((page.Uri.AbsoluteUri, text));
    }

    // HTML -> prosa: AngleSharp per il DOM, poi il testo del body a spazi normalizzati.
    private static string ExtractText(string html)
    {
        try
        {
            using var doc = new HtmlParser().ParseDocument(html);
            string text = doc.Body?.TextContent ?? "";
            return SpaceRegex().Replace(text, " ").Trim();
        }
        catch
        {
            return "";
        }
    }

    // Le lezioni, una alla volta: il canale fa da ammortizzatore tra il ritmo
    // del crawler e quello dell'apprendimento.
    private async Task LearnLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (url, text) in _pages.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await service.LearnFromPageAsync(text, ct);
                    logger.LogDebug("LLM: assorbita {Url} ({Chars} caratteri)", url, text.Length);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "LLM: pagina {Url} non assorbita", url);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
