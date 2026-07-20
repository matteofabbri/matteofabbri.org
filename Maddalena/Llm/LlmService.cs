using System.Numerics;
using Microsoft.Extensions.Options;
using PredictiveCoding.LLM;

namespace Maddalena.Llm;

/// <summary>Gli stati del sottosistema LLM, dal boot al servizio.</summary>
public enum LlmState { Starting, Training, Ready, Off, Failed }

// Il PredictiveLlm come SINGLETON del sito: un solo modello in memoria, vivo
// per tutta la vita del processo, condiviso tra worker e richieste HTTP.
// Il modello NON e' thread-safe (Generate tocca il piano latente e l'odometro,
// Learn/Train muovono i pesi), quindi ogni operazione passa dal semaforo: una
// alla volta, le altre in coda. Chi arriva mentre non e' pronto riceve
// LlmNotReadyException (il controller la traduce in 503).
//
// PERSISTENZA: i pesi vivono in RAM; PredictiveLlm.Save li scrive (formato
// FASTER) nella cartella di stato — in prod il volume docker maddalena-llm
// (Llm__StateDir=/state/llm dall'AppHost), in dev ContentRoot/llm-state.
public sealed class LlmService(
    IOptions<LlmOptions> options,
    IHostEnvironment environment,
    ILogger<LlmService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PredictiveLlm? _llm;

    // Alzato da ogni apprendimento non ancora scritto su disco; il timer del
    // worker (SaveIfDirtyAsync) lo riabbassa. Teach e Train salvano subito da soli.
    private bool _dirty;
    private long _pagesLearned;

    /// <summary>La cartella dove il modello si salva e riprende.</summary>
    public string StateDir { get; } =
        string.IsNullOrWhiteSpace(options.Value.StateDir)
            ? Path.Combine(environment.ContentRootPath, "llm-state")
            : options.Value.StateDir;

    public LlmState State { get; private set; } = LlmState.Starting;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSavedAt { get; private set; }
    public bool Resumed { get; private set; }
    public string? CorpusSummary { get; private set; }

    // ── Ciclo di vita (chiamate dal worker) ──────────────────────────────────

    internal void Adopt(PredictiveLlm llm, bool resumed, DateTimeOffset? lastSavedAt)
    {
        _llm = llm;
        Resumed = resumed;
        LastSavedAt = lastSavedAt;
        State = LlmState.Training;   // adottato ma non ancora esposto: prima il giro di training
    }

    internal void MarkReady() => State = LlmState.Ready;

    internal void MarkOff() => State = LlmState.Off;

    internal void MarkFailed(string error)
    {
        State = LlmState.Failed;
        LastError = error;
    }

    /// <summary>
    /// Il giro di training di avvio: assorbe il corpus delle datasources e
    /// salva lo stato. Tra un'epoca e l'altra controlla il token: allo shutdown
    /// il training si interrompe li' invece di trattenere il processo.
    /// </summary>
    internal async Task TrainAsync(string corpus, string corpusSummary, CancellationToken ct)
    {
        var llm = _llm ?? throw new InvalidOperationException("Nessun modello adottato.");
        await _gate.WaitAsync(ct);
        try
        {
            var report = llm.Train(corpus, new TrainingOptions
            {
                Epochs = options.Value.Epochs,
                MaxWindows = options.Value.MaxWindows,
                OnEpochEnd = (epoch, meanLoss) =>
                {
                    ct.ThrowIfCancellationRequested();
                    logger.LogInformation("LLM training: epoca {Epoch}, loss media {Loss:F3}", epoch, meanLoss);
                },
            });
            CorpusSummary = corpusSummary;
            logger.LogInformation(
                "LLM training finito: perplexity {Before:F1} -> {After:F1} in {Steps} passi su {Windows} finestre",
                report.PerplexityBefore, report.PerplexityAfter, report.OptimizerSteps, report.TrainWindows);

            SaveToDisk(llm);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Interroga ────────────────────────────────────────────────────────────

    public async Task<GenerationResult> GenerateAsync(string prompt, int maxNewTokens, CancellationToken ct)
    {
        var llm = ReadyLlm();
        await _gate.WaitAsync(ct);
        try
        {
            return llm.GenerateDetailed(prompt, maxNewTokens);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Istruisci ────────────────────────────────────────────────────────────

    /// <summary>
    /// Apprendimento online: il modello assorbe il testo adesso (qualche passo
    /// di discesa con l'ottimizzatore persistente) e lo stato va subito su
    /// disco — un insegnamento non deve sparire al riavvio del container.
    /// </summary>
    public async Task<OnlineLearnReport> TeachAsync(string text, CancellationToken ct)
    {
        var llm = ReadyLlm();
        await _gate.WaitAsync(ct);
        try
        {
            var report = llm.Learn(text, options.Value.TeachSteps, options.Value.TeachLearningRate);
            SaveToDisk(llm);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Impara dal crawler ───────────────────────────────────────────────────

    /// <summary>Pagine del crawler assorbite in questa vita del processo.</summary>
    public long PagesLearned => Interlocked.Read(ref _pagesLearned);

    /// <summary>
    /// Una pagina crawlata diventa una lezione. NIENTE salvataggio qui: il
    /// crawler macina in continuo, scrivere il disco a ogni pagina sarebbe un
    /// martello — ci pensa il timer del worker (SaveIfDirtyAsync).
    /// </summary>
    internal async Task LearnFromPageAsync(string text, CancellationToken ct)
    {
        var llm = ReadyLlm();
        await _gate.WaitAsync(ct);
        try
        {
            llm.Learn(text, options.Value.CrawlLearnSteps, options.Value.TeachLearningRate);
            _dirty = true;
        }
        finally
        {
            _gate.Release();
        }
        Interlocked.Increment(ref _pagesLearned);
    }

    /// <summary>Il salvataggio del timer: scrive solo se c'e' qualcosa di nuovo. True se ha scritto.</summary>
    internal async Task<bool> SaveIfDirtyAsync(CancellationToken ct)
    {
        if (!_dirty) return false;
        var llm = _llm;
        if (llm is null) return false;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_dirty) return false;
            SaveToDisk(llm);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Stato (senza semaforo: numeri informativi, una lettura sporca non fa danni) ──

    // I contatori sono BigInteger (l'odometro di un modello eterno non deve mai
    // traboccare): verso l'API diventano stringhe, mai number JSON.
    public (long ParameterCount, BigInteger Trained, BigInteger Evaluated, BigInteger Absorbed, BigInteger Prompted, BigInteger Generated)? Numbers()
    {
        var llm = _llm;
        if (llm is null) return null;
        var o = llm.Odometer;
        return (llm.ParameterCount, o.Trained, o.Evaluated, o.Absorbed, o.Prompted, o.Generated);
    }

    private PredictiveLlm ReadyLlm() =>
        State == LlmState.Ready && _llm is not null
            ? _llm
            : throw new LlmNotReadyException(State);

    // ── Salvataggio ──────────────────────────────────────────────────────────

    // Scrive di fianco e poi scambia le cartelle: se il processo muore a meta'
    // Save, lo stato buono resta intatto (al peggio si perde solo quest'ultimo
    // salvataggio). Chiamare SOLO sotto il semaforo: Save legge i pesi, e i
    // pesi non devono muoversi mentre li si fotografa.
    private void SaveToDisk(PredictiveLlm llm)
    {
        string tmp = StateDir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        llm.Save(tmp);
        if (Directory.Exists(StateDir)) Directory.Delete(StateDir, recursive: true);
        Directory.Move(tmp, StateDir);
        LastSavedAt = DateTimeOffset.UtcNow;
        _dirty = false;
    }
}

/// <summary>Il modello non e' (ancora) interrogabile: il controller risponde 503.</summary>
public sealed class LlmNotReadyException(LlmState state)
    : InvalidOperationException($"LLM non pronto (stato: {state}).")
{
    public LlmState State { get; } = state;
}
