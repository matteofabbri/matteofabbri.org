using Microsoft.Extensions.Options;

namespace MainSite.RequestLogging;

/// <summary>
/// One-shot rebuild of the request log's scalar indexes from the payloads.
///
/// The pinned Sperone version can lose an overflow chunk of a scalar B-tree
/// node, after which every insert and every update of the collection throws and
/// the request log stops. <see cref="RequestLogStore"/> drops the corrupt store
/// on the same start; this job walks the element store (keys only, no index)
/// page by page and rewrites every row's indexing specification, so the index
/// grows back from the payloads without touching them.
///
/// It runs next to the live writer, so a page can overlap a few new rows; a
/// failed row does not stop the pass and the marker is written only when the
/// whole collection went through. Enabled with RequestLog:ReindexIndexes.
/// </summary>
public sealed class RequestLogReindex : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private const int PageSize = 500;
    private const string MarkerFileName = "indexes-reindexed.json";

    private readonly RequestLogOptions _options;
    private readonly RequestLogStore _store;
    private readonly ILogger<RequestLogReindex> _logger;

    public RequestLogReindex(
        IOptions<RequestLogOptions> options,
        RequestLogStore store,
        ILogger<RequestLogReindex> logger)
    {
        _options = options.Value;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.ReindexIndexes)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            string marker = Path.Combine(_store.ResolvedPath, MarkerFileName);
            if (File.Exists(marker))
            {
                return;
            }

            _logger.LogInformation("Reindex richieste: ricostruzione degli indici scalari iniziata");

            int skip = 0;
            int scanned = 0;
            int updated = 0;
            int failed = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                var page = await _store.ReindexPageAsync(skip, PageSize, stoppingToken);
                if (page.Scanned == 0)
                {
                    break;
                }

                skip += page.Scanned;
                scanned += page.Scanned;
                updated += page.Updated;
                failed += page.Failed;

                if (scanned % (PageSize * 20) == 0)
                {
                    _logger.LogInformation(
                        "Reindex richieste: {Scanned} righe lette, {Updated} reindicizzate, {Failed} fallite",
                        scanned,
                        updated,
                        failed);
                }
            }

            if (failed == 0)
            {
                Directory.CreateDirectory(_store.ResolvedPath);
                File.WriteAllText(
                    marker,
                    "{\"completedAt\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}");
                _logger.LogInformation(
                    "Reindex richieste completato: {Scanned} righe lette, {Updated} reindicizzate",
                    scanned,
                    updated);
            }
            else
            {
                _logger.LogWarning(
                    "Reindex richieste: {Failed} righe non reindicizzate; il prossimo avvio riprova",
                    failed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown: no marker, the next start resumes from zero.
        }
        catch (Exception ex)
        {
            // A maintenance job must never take the site down.
            _logger.LogError(ex, "Reindex richieste interrotto; il prossimo avvio riprova");
        }
    }
}
