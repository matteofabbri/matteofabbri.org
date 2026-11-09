using System.Text.Json;
using BotDetection.Client;
using Microsoft.Extensions.Options;

namespace MainSite.RequestLogging;

/// <summary>
/// One-shot backfill of the bot flags on entries already stored in Sperone.
///
/// Runs in-process (the site is the only owner of the embedded database), one
/// day at a time, in chronological order, so the 60-second rate window is still
/// meaningful for historical rows. Enabled with RequestLog:BackfillBotFlags;
/// entries that already carry both flags are skipped, which makes the job
/// resumable and safe to leave configured by mistake.
/// </summary>
public sealed class RequestLogBackfill : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    private readonly RequestLogOptions _options;
    private readonly RequestLogStore _store;
    private readonly BotClassifier _classifier;
    private readonly ILogger<RequestLogBackfill> _logger;

    public RequestLogBackfill(
        IOptions<RequestLogOptions> options,
        RequestLogStore store,
        BotClassifier classifier,
        ILogger<RequestLogBackfill> logger)
    {
        _options = options.Value;
        _store = store;
        _classifier = classifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.BackfillBotFlags)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // A private rate tracker: the live writer has its own, and feeding it
        // historical rows would corrupt the current window.
        var enricher = new BotEnricher(_classifier, new ClientRateTracker());

        DateTimeOffset to = DateTimeOffset.UtcNow;
        DateTimeOffset? previous = _options.BackfillForce ? null : ReadMarker();
        DateTimeOffset from = previous ?? to.AddDays(-Math.Max(_options.BackfillDays, 1));
        if (from >= to)
        {
            _logger.LogInformation("Bot flags backfill: already done at {To:O}; nothing to do", to);
            return;
        }

        int scanned = 0;
        int updated = 0;
        int skipped = 0;
        int unresolved = 0;

        _logger.LogInformation(
            "Bot flags backfill: {From:O} → {To:O} (force={Force})",
            from,
            to,
            _options.BackfillForce);

        for (DateTimeOffset windowStart = from;
             windowStart < to && !stoppingToken.IsCancellationRequested;
             windowStart = windowStart.AddDays(1))
        {
            DateTimeOffset windowEnd = windowStart.AddDays(1);
            var entries = await _store.GetEntriesAsync(windowStart, windowEnd, stoppingToken);
            if (entries.Count == 0)
            {
                continue;
            }

            var ordered = entries.OrderBy(entry => entry.Timestamp).ToList();
            scanned += ordered.Count;

            foreach (var chunk in ordered.Chunk(_options.EffectiveBatchSize))
            {
                var candidateIds = chunk
                    .Where(NeedsClassification)
                    .Select(entry => entry.Id)
                    .ToHashSet(StringComparer.Ordinal);

                if (candidateIds.Count == 0)
                {
                    skipped += chunk.Length;
                    continue;
                }

                var enriched = await enricher.EnrichAsync(chunk, stoppingToken, NeedsClassification);
                var toUpdate = enriched
                    .Where(entry => candidateIds.Contains(entry.Id)
                        && entry.IsBot is not null
                        && entry.IsMaliciousURL is not null)
                    .ToList();

                updated += await _store.UpdateManyAsync(toUpdate, stoppingToken);
                skipped += chunk.Length - toUpdate.Count;
                // The model may still be starting when the site comes up: a
                // candidate that produced no verdict is not lost, it just means
                // the run cannot be marked as complete.
                unresolved += candidateIds.Count - toUpdate.Count;
            }

            _logger.LogInformation(
                "Bot flags backfill: {Day:yyyy-MM-dd} done ({Scanned} scanned, {Updated} updated)",
                windowStart,
                scanned,
                updated);
        }

        _logger.LogInformation(
            "Bot flags backfill finished: {Scanned} scanned, {Updated} updated, {Skipped} skipped, {Unresolved} unresolved",
            scanned,
            updated,
            skipped,
            unresolved);

        if (unresolved == 0)
        {
            WriteMarker(to);
        }
        else
        {
            // No marker: the next start scans again, skipping the rows that
            // already carry flags, and retries only the unresolved ones.
            _logger.LogWarning(
                "Bot flags backfill left {Unresolved} rows unclassified (model unavailable?); the next start will retry",
                unresolved);
        }
    }

    private bool NeedsClassification(RequestLogEntry entry)
        => _options.BackfillForce || entry.IsBot is null || entry.IsMaliciousURL is null;

    // One small file next to the Sperone database. A marker means the full
    // window was already processed: a restart (or a second deploy with the flag
    // still on) only looks at rows written after that moment, which the live
    // writer has already labelled.
    private string MarkerPath => Path.Combine(_store.ResolvedPath, "bot-backfill.json");

    private DateTimeOffset? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath))
            {
                return null;
            }

            var marker = JsonSerializer.Deserialize<BackfillMarker>(File.ReadAllText(MarkerPath));
            return marker?.To;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the bot backfill marker; running the full window");
            return null;
        }
    }

    private void WriteMarker(DateTimeOffset to)
    {
        try
        {
            var marker = new BackfillMarker(to, DateTimeOffset.UtcNow);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the bot backfill marker; the next start will rescan");
        }
    }

    private sealed record BackfillMarker(DateTimeOffset To, DateTimeOffset CompletedAt);
}
