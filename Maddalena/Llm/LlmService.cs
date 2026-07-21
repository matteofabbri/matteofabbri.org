using System.Numerics;
using Microsoft.Extensions.Options;
using PredictiveCoding.LLM;
using PredictiveCoding.LLM.Serving;

namespace Maddalena.Llm;

/// <summary>Gli stati del sottosistema LLM, dal boot al servizio.</summary>
public enum LlmState { Starting, Training, Ready, Off, Failed }

// Il PredictiveLlm del sito, in DUE copie con ruoli opposti.
//
//   LO SCRITTORE (_writer) — il modello che impara. Crawler, teach e training
//   d'avvio ci passano uno alla volta, dietro _writeGate. Un solo thread per
//   volta perche' il modello NON e' thread-safe in scrittura, e non per
//   distrazione: in Autograd i gradienti vivono DENTRO il peso (Tensor.Grad e'
//   un array per-parametro, non per-chiamata), quindi due backward concorrenti
//   non si corrompono — si SOMMANO, e l'ottimizzatore applica una direzione che
//   non e' quella di nessuno dei due. Silenziosamente, senza eccezioni.
//
//   IL LETTORE (_reader) — una copia congelata dei pesi. Nessuno la muove,
//   quindi le richieste HTTP la leggono in parallelo SENZA semaforo: generare
//   e' solo forward (SelfSpeculative.Generate non chiama mai Backward, e le op
//   di Autograd allocano ognuna il proprio output — nessun buffer statico).
//
// Ogni tanto — dopo il training d'avvio, dopo un teach, e a ogni salvataggio
// periodico — si fotografano i pesi dello scrittore in un lettore nuovo e si
// scambia il riferimento. Chi sta gia' generando finisce sulla copia vecchia
// (che resta valida e intatta finche' lo fa); i nuovi arrivati prendono quella
// fresca. Nessuno aspetta nessuno.
//
// PERSISTENZA: i pesi vivono in RAM; PredictiveLlm.Save li scrive (formato
// FASTER) nella cartella di stato — in prod il volume docker maddalena-llm
// (Llm__StateDir=/state/llm dall'AppHost), in dev ContentRoot/llm-state.
public sealed class LlmService(
    IOptions<LlmOptions> options,
    IHostEnvironment environment,
    ILogger<LlmService> logger)
{
    // ── Lo scrittore: uno alla volta ─────────────────────────────────────────
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private PredictiveLlm? _writer;

    // ── Il lettore: copia congelata, letta senza lock ────────────────────────
    // Volatile perche' il riferimento viene scambiato da un thread e letto da
    // altri: senza barriera un lettore potrebbe vedere il puntatore nuovo prima
    // delle scritture che l'hanno riempito.
    //
    // NON e' un PredictiveLlm ma un LlmServer: lo stack di inferenza con la KV
    // CACHE. Lo scrittore, per generare, rifa' il forward sull'INTERA sequenza
    // a ogni token prodotto — "ciao" piu' 64 token di risposta gli costa ~2.300
    // posizioni elaborate. Il server tiene in cache le chiavi/valori delle
    // posizioni gia' viste, quindi ogni token nuovo costa UN passo: ~69 in
    // tutto, e il divario cresce col quadrato della lunghezza. In piu' lo stack
    // servito non e' autograd: niente buffer dei gradienti, meta' memoria.
    private LlmServer? _reader;

    // Non e' mutua esclusione ma un TETTO: senza semaforo mille richieste
    // concorrenti sono mille forward paralleli, e ognuno alloca il proprio
    // nastro di attivazioni. La macchina finirebbe la RAM prima della CPU.
    private readonly SemaphoreSlim _readSlots = MakeReadSlots(options.Value);

    private static SemaphoreSlim MakeReadSlots(LlmOptions o)
    {
        int slots = o.MaxConcurrentGenerations > 0
            ? o.MaxConcurrentGenerations
            : Math.Max(1, Environment.ProcessorCount / 2);
        return new SemaphoreSlim(slots, slots);
    }

    // I token macinati dalla generazione. NON possono stare sull'odometro del
    // lettore: quello e' una copia usa-e-getta, buttata al prossimo scambio.
    // Si accumulano qui con Interlocked (i lettori sono concorrenti) e vengono
    // travasati nell'odometro dello scrittore a ogni salvataggio, cosi'
    // finiscono nel checkpoint invece di svanire al riavvio.
    private long _pendingPrompted;
    private long _pendingGenerated;

    // Alzato da ogni apprendimento non ancora scritto su disco; il timer del
    // worker (SaveIfDirtyAsync) lo riabbassa. Teach e Train salvano subito da
    // soli. VOLATILE: lo alza il thread delle lezioni e lo legge quello del
    // timer, che senza barriera potrebbe non vederlo cambiare mai.
    private volatile bool _dirty;
    private long _pagesLearned;

    /// <summary>La cartella dove il modello si salva e riprende.</summary>
    public string StateDir { get; } = ResolveStateDir(options.Value, environment);

    /// <summary>La cartella degli shard del corpus (sorella di StateDir, mai figlia).</summary>
    public string CorpusDir { get; } = ResolveCorpusDir(options.Value, environment);

    private static string ResolveStateDir(LlmOptions o, IHostEnvironment env) =>
        string.IsNullOrWhiteSpace(o.StateDir)
            ? Path.Combine(env.ContentRootPath, "llm-state")
            : o.StateDir;

    // Il corpus vive ACCANTO allo stato, non dentro: SaveToDisk cancella e
    // ricrea StateDir a ogni salvataggio, e un corpus li' dentro morirebbe ogni
    // cinque minuti. Da fratello finisce comunque dentro al volume persistente
    // — in produzione StateDir e' /state/llm e il volume docker e' montato su
    // /state, quindi /state/llm-corpus e' gia' al sicuro senza configurare
    // niente; in dev diventa ContentRoot/llm-corpus. Llm__CorpusDir resta come
    // override per metterlo altrove (un disco piu' grande, per esempio).
    private static string ResolveCorpusDir(LlmOptions o, IHostEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(o.CorpusDir)) return o.CorpusDir;

        string state  = ResolveStateDir(o, env);
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(state));
        return string.IsNullOrEmpty(parent)
            ? Path.Combine(env.ContentRootPath, "llm-corpus")   // StateDir era una radice: non ha un fratello
            : Path.Combine(parent, "llm-corpus");
    }

    // Lo stato lo scrive il worker e lo legge il controller, da thread diversi.
    // volatile su un int (l'enum sottostante) invece che su una proprieta':
    // la scrittura fa anche da barriera per i campi riempiti appena prima
    // (LastError, CorpusSummary), che cosi' non possono apparire dopo lo stato.
    private volatile int _state = (int)LlmState.Starting;
    public LlmState State => (LlmState)_state;

    public string? LastError { get; private set; }
    public bool Resumed { get; private set; }
    public string? CorpusSummary { get; private set; }

    // Le date vivono come tick in un long: DateTimeOffset? e' una struct troppo
    // grande per essere letta e scritta in modo atomico, e un lettore
    // concorrente potrebbe pescarne meta' vecchia e meta' nuova. 0 = mai.
    private long _lastSavedAtTicks;
    private long _snapshotAtTicks;

    public DateTimeOffset? LastSavedAt => TicksToDate(Interlocked.Read(ref _lastSavedAtTicks));

    /// <summary>Quando il lettore e' stato allineato ai pesi dello scrittore l'ultima volta.</summary>
    public DateTimeOffset? SnapshotAt => TicksToDate(Interlocked.Read(ref _snapshotAtTicks));

    private static DateTimeOffset? TicksToDate(long ticks) =>
        ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);

    // ── Ciclo di vita (chiamate dal worker) ──────────────────────────────────

    internal void Adopt(PredictiveLlm llm, bool resumed, DateTimeOffset? lastSavedAt)
    {
        _writer = llm;
        // Parametri del training PC (iPC): valgono anche al resume.
        if (options.Value.PcAnchorEvery is { } anchorEvery)
            llm.PcAnchorEvery = anchorEvery;   // null = tieni il default (unico) della libreria
        llm.PcAnchorBurst = options.Value.PcAnchorBurst;
        llm.PcEtaState    = options.Value.PcEtaState;
        llm.PcLrWeights   = options.Value.PcLrWeights;
        llm.PcClipNorm    = options.Value.PcClipNorm;
        llm.PcRelaxSteps  = options.Value.PcRelaxSteps;
        Resumed = resumed;
        Interlocked.Exchange(ref _lastSavedAtTicks, lastSavedAt?.UtcTicks ?? 0);
        _state = (int)LlmState.Training;   // adottato ma non ancora esposto: prima il giro di training
    }

    /// <summary>
    /// Apre /api/llm. Prima pubblica il lettore: senza copia dei pesi non c'e'
    /// niente da servire, e uno stato Ready senza lettore darebbe 503 a tutti.
    /// </summary>
    internal void MarkReady()
    {
        PublishSnapshot();
        _state = (int)LlmState.Ready;
    }

    internal void MarkOff() => _state = (int)LlmState.Off;

    internal void MarkFailed(string error)
    {
        LastError = error;                  // prima il dettaglio...
        _state = (int)LlmState.Failed;      // ...poi lo stato: la scrittura volatile pubblica anche il resto
    }

    /// <summary>
    /// Il giro di training di avvio: assorbe il corpus-seme e salva lo stato.
    /// Tra un'epoca e l'altra controlla il token: allo shutdown il training si
    /// interrompe li' invece di trattenere il processo.
    /// </summary>
    internal async Task TrainAsync(string corpus, string corpusSummary, CancellationToken ct)
    {
        var llm = _writer ?? throw new InvalidOperationException("Nessun modello adottato.");
        await _writeGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var r = llm.LearnPc(corpus, steps: options.Value.Epochs, countAsTraining: true);
            logger.LogInformation("LLM training PC (iPC) finito: sorpresa {Before:F3} -> {After:F3} in {Epochs} epoche",
                r.SurpriseBefore, r.SurpriseAfter, r.Steps);
            CorpusSummary = corpusSummary;
            SaveToDisk(llm);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ── Interroga: il percorso SENZA semaforo ────────────────────────────────

    /// <summary>
    /// Genera leggendo la copia congelata. Non tocca lo scrittore, quindi non
    /// aspetta ne' il crawler ne' il salvataggio: la lezione in corso e questa
    /// richiesta girano davvero insieme.
    /// </summary>
    public async Task<GenerationResult> GenerateAsync(string prompt, int maxNewTokens, CancellationToken ct)
    {
        var server = ReaderOrThrow();
        await _readSlots.WaitAsync(ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Il forward e' sincrono e lungo: fuori dal thread della richiesta.
            string text = await Task.Run(() => server.Generate(prompt, maxNewTokens, ReadOnlyGeneration), ct);

            // Contati qui, non presi dall'odometro del lettore (che e' una copia
            // usa-e-getta condivisa: un BigInteger += concorrente perde colpi).
            long promptTokens = CountTokens(prompt);
            long newTokens    = CountTokens(text);
            Interlocked.Add(ref _pendingPrompted,  promptTokens);
            Interlocked.Add(ref _pendingGenerated, newTokens);

            // Col KV cache un forward = un token (prefill + generati), invece
            // della passata completa per token del percorso allenabile. Drafted
            // e Accepted restano a zero: la decodifica speculativa vive sullo
            // stack allenabile e non ha un gemello lato serving.
            int forwards = (int)Math.Min(int.MaxValue, promptTokens + newTokens);

            logger.LogInformation("Generate: prompt {PromptChars} char -> {Forwards} forward in {Ms} ms",
                prompt?.Length ?? 0, forwards, sw.ElapsedMilliseconds);
            return new GenerationResult(text, forwards, Drafted: 0, Accepted: 0);
        }
        finally
        {
            _readSlots.Release();
        }
    }

    // Opzioni di generazione ESPLICITAMENTE inerti: il lettore e' condiviso da
    // piu' thread e non deve scrivere niente. I default della libreria sono gia'
    // questi, ma scriverli qui vuol dire che un domani cambiarli la' non
    // trasforma di nascosto il lettore in uno scrittore.
    private static readonly GenerationOptions ReadOnlyGeneration = new()
    {
        LearnFromPrompt = false,   // niente Learn(): muoverebbe i pesi sotto gli altri lettori
        SettlePlanSteps = 0,       // niente SettlePlan/SetPlan: _plan e' unico per modello
        UseMemory       = false,   // niente EpisodicMemory: ha stato mutabile suo
    };

    // Il tokenizer si prende dallo scrittore: e' lo STESSO oggetto che il
    // lettore ha ricevuto all'export (immutabile, condiviso apposta).
    private long CountTokens(string? text) =>
        string.IsNullOrEmpty(text) || _writer is not { } w
            ? 0
            : w.Tokenizer.Encode(text, addBos: false).Count;

    // ── Istruisci ────────────────────────────────────────────────────────────

    /// <summary>
    /// Apprendimento online: il modello assorbe il testo adesso (qualche passo
    /// di discesa PC), lo stato va subito su disco e il lettore viene allineato
    /// — una lezione appena data deve vedersi alla richiesta successiva.
    /// </summary>
    public async Task<OnlineLearnReport> TeachAsync(string text, CancellationToken ct)
    {
        var llm = WriterOrThrow();
        OnlineLearnReport report;
        await _writeGate.WaitAsync(ct);
        try
        {
            report = llm.LearnPc(text, options.Value.TeachSteps);
            SaveToDisk(llm);
            logger.LogInformation("Teach: {Chars} char, sorpresa {Before:F3} -> {After:F3} in {Steps} step",
                text?.Length ?? 0, report.SurpriseBefore, report.SurpriseAfter, report.Steps);
        }
        finally
        {
            _writeGate.Release();
        }
        PublishSnapshot();
        return report;
    }

    // ── Impara dal crawler ───────────────────────────────────────────────────

    /// <summary>Pagine del crawler assorbite in questa vita del processo.</summary>
    public long PagesLearned => Interlocked.Read(ref _pagesLearned);

    // Quanto testo il crawler ha sedimentato su disco: il numero che dice
    // quando c'e' abbastanza materiale per addestrare un tokenizer vero (e,
    // piu' avanti, per un pretraining sulla scheda).
    private long _corpusBytes;
    private long _corpusDocuments;

    public long CorpusBytes => Interlocked.Read(ref _corpusBytes);
    public long CorpusDocuments => Interlocked.Read(ref _corpusDocuments);

    internal void ReportCorpus(long bytes, long documents)
    {
        Interlocked.Exchange(ref _corpusBytes, bytes);
        Interlocked.Exchange(ref _corpusDocuments, documents);
    }

    /// <summary>
    /// Una pagina crawlata diventa una lezione, A BOCCONI: il testo si spezza in
    /// blocchi e il semaforo si rilascia tra uno e l'altro. Non serve piu' a far
    /// passare le generazioni (quelle ormai non passano di qui), ma a non tenere
    /// lo scrittore occupato per mezz'ora di fila: teach, salvataggio periodico
    /// e shutdown trovano una fessura invece di aspettare la pagina intera.
    /// NIENTE salvataggio qui: ci pensa il timer del worker (SaveIfDirtyAsync).
    /// </summary>
    internal async Task LearnFromPageAsync(string text, CancellationToken ct)
    {
        var llm = WriterOrThrow();
        int chunk = Math.Max(256, options.Value.CrawlLearnChunkChars);

        for (int start = 0; start < text.Length;)
        {
            ct.ThrowIfCancellationRequested();
            int end = NextCut(text, start, chunk);
            string boccone = text[start..end];
            start = end;

            await _writeGate.WaitAsync(ct);
            try
            {
                // measureSurprise:false -> niente forward before/after (il crawler scarta
                // il report): ~1/3 di lavoro in meno per pagina.
                llm.LearnPc(boccone, options.Value.CrawlLearnSteps, measureSurprise: false);
                _dirty = true;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        var pages = Interlocked.Increment(ref _pagesLearned);
        logger.LogInformation("Impara dal crawler: {Chars} char (pagine totali {Pages})", text?.Length ?? 0, pages);
    }

    // Dove finisce il boccone: al piu' `chunk` caratteri, ma arretrando
    // all'ultimo spazio utile — una lezione non deve cominciare a meta' di una
    // parola. E mai in mezzo a una coppia surrogata (un'emoji spezzata a meta'
    // diventa un carattere di sostituzione, cioe' rumore insegnato per vero).
    private static int NextCut(string text, int start, int chunk)
    {
        int end = Math.Min(start + chunk, text.Length);
        if (end == text.Length) return end;

        int space = text.LastIndexOf(' ', end - 1, Math.Min(chunk / 4, end - start));
        if (space > start) return space + 1;

        if (char.IsHighSurrogate(text[end - 1])) end--;   // niente meta' emoji
        return Math.Max(end, start + 1);                  // sempre un passo avanti: niente loop infinito
    }

    /// <summary>Il salvataggio del timer: scrive solo se c'e' qualcosa di nuovo. True se ha scritto.</summary>
    internal async Task<bool> SaveIfDirtyAsync(CancellationToken ct)
    {
        if (!_dirty) return false;
        var llm = _writer;
        if (llm is null) return false;

        await _writeGate.WaitAsync(ct);
        try
        {
            if (!_dirty) return false;
            SaveToDisk(llm);
        }
        finally
        {
            _writeGate.Release();
        }

        // Il lettore si allinea al ritmo del salvataggio: chi interroga vede
        // quello che il crawler ha imparato dall'ultima scrittura in poi.
        PublishSnapshot();
        return true;
    }

    // ── Lo scambio lettore/scrittore ─────────────────────────────────────────

    /// <summary>
    /// Esporta i pesi dello scrittore sullo stack di inferenza (KV cache) e lo
    /// mette al posto del lettore.
    /// </summary>
    /// <remarks>
    /// FUORI dal semaforo, deliberatamente. L'export legge i pesi mentre lo
    /// scrittore potrebbe aggiornarli, quindi la fotografia puo' venire
    /// "mossa": qualche tensore da prima di un update e qualche altro da dopo.
    /// Perche' va bene:
    ///   - non e' un problema di memoria — i float allineati si leggono in modo
    ///     atomico e gli array non vengono mai riallocati: nessun valore a
    ///     meta', nessun crash, solo valori di epoche diverse;
    ///   - la finestra e' minuscola in confronto al ritmo dell'apprendimento —
    ///     l'export dura secondi, un boccone da 4 KB su CPU ne dura centinaia:
    ///     nel mezzo cade zero o un update;
    ///   - e un update iPC e' un passo piccolo (clip a 1.0). La differenza tra
    ///     la foto mossa e quella ferma e' dell'ordine di UN passo di gradiente,
    ///     su un modello che ne fa migliaia.
    /// Il costo evitato invece e' certo: tenere il semaforo per tutto l'export
    /// fermerebbe il crawler ogni volta, per secondi.
    /// </remarks>
    private void PublishSnapshot()
    {
        var src = _writer;
        if (src is null) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // CreateServer condivide il tokenizer con lo scrittore (immutabile:
            // il ByteTokenizer e' senza stato, il BPE ha una cache concorrente).
            var served = src.CreateServer();

            // Da qui in poi le richieste nuove prendono la copia fresca; quelle
            // in corso finiscono tranquille sulla vecchia, che nessuno muove.
            Volatile.Write(ref _reader, served);
            Interlocked.Exchange(ref _snapshotAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            logger.LogInformation("LLM: lettore allineato allo scrittore ({Parameters:N0} parametri) in {Ms} ms",
                src.ParameterCount, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // L'export non deve MAI far cadere chi lo ha chiesto. Se getta —
            // una config che lo stack di serving non sa esportare, memoria
            // finita — si perde la generazione (l'API risponde 503 finche' non
            // riesce), ma il crawler continua a imparare e il modello continua
            // a salvarsi. Degradato, non morto: senza questo catch, un export
            // fallito all'avvio spegnerebbe l'intero sottosistema LLM.
            logger.LogError(ex, "LLM: export del lettore fallito, la generazione resta sul lettore precedente");
        }
    }

    // ── Stato (senza semaforo: numeri informativi, una lettura sporca non fa danni) ──

    // I contatori sono BigInteger (l'odometro di un modello eterno non deve mai
    // traboccare): verso l'API diventano stringhe, mai number JSON.
    public (long ParameterCount, BigInteger Trained, BigInteger Evaluated, BigInteger Absorbed, BigInteger Prompted, BigInteger Generated)? Numbers()
    {
        var llm = _writer;
        if (llm is null) return null;
        var o = llm.Odometer;
        // Ai numeri dello scrittore si sommano i token di generazione non ancora
        // travasati: l'odometro che si vede e' sempre quello vero, non quello
        // dell'ultimo salvataggio.
        return (llm.ParameterCount, o.Trained, o.Evaluated, o.Absorbed,
                o.Prompted  + Interlocked.Read(ref _pendingPrompted),
                o.Generated + Interlocked.Read(ref _pendingGenerated));
    }

    private LlmServer ReaderOrThrow() =>
        State == LlmState.Ready && Volatile.Read(ref _reader) is { } r
            ? r
            : throw new LlmNotReadyException(State);

    private PredictiveLlm WriterOrThrow() =>
        State == LlmState.Ready && _writer is { } w
            ? w
            : throw new LlmNotReadyException(State);

    // ── Salvataggio ──────────────────────────────────────────────────────────

    // Scrive di fianco e poi scambia le cartelle: se il processo muore a meta'
    // Save, lo stato buono resta intatto (al peggio si perde solo quest'ultimo
    // salvataggio). Chiamare SOLO sotto il semaforo: Save legge i pesi, e i
    // pesi non devono muoversi mentre li si fotografa.
    private void SaveToDisk(PredictiveLlm llm)
    {
        // I token della generazione entrano nell'odometro adesso, cosi' il
        // checkpoint se li porta dietro. Exchange: quello che travaso lo azzero
        // in un colpo solo, senza perdere gli incrementi che arrivano intanto.
        // Siamo sotto _writeGate, l'unico posto da cui si tocca l'odometro.
        llm.Odometer.AddServed(
            Interlocked.Exchange(ref _pendingPrompted, 0),
            Interlocked.Exchange(ref _pendingGenerated, 0));

        string tmp = StateDir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        llm.Save(tmp);
        if (Directory.Exists(StateDir)) Directory.Delete(StateDir, recursive: true);
        Directory.Move(tmp, StateDir);
        Interlocked.Exchange(ref _lastSavedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        _dirty = false;
    }
}

/// <summary>Il modello non e' (ancora) interrogabile: il controller risponde 503.</summary>
public sealed class LlmNotReadyException(LlmState state)
    : InvalidOperationException($"LLM non pronto (stato: {state}).")
{
    public LlmState State { get; } = state;
}
