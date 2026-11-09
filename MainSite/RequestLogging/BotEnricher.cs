using BotDetection.Client;
using BotDetection.Contracts.V1;

namespace MainSite.RequestLogging;

/// <summary>
/// Labels a batch of request-log entries before they are written to Sperone.
///
/// This runs in the request-log writer, not in the HTTP pipeline: the site
/// never blocks, never waits for the model, and a model outage only leaves the
/// flags null. The rate features come from the same stream of entries that is
/// about to be stored.
/// </summary>
public sealed class BotEnricher
{
    private readonly BotClassifier _classifier;
    private readonly ClientRateTracker _rates;

    public BotEnricher(BotClassifier classifier, ClientRateTracker rates)
    {
        _classifier = classifier;
        _rates = rates;
    }

    public async Task<IReadOnlyList<RequestLogEntry>> EnrichAsync(
        IReadOnlyList<RequestLogEntry> entries,
        CancellationToken cancellationToken,
        Func<RequestLogEntry, bool>? needsClassification = null)
    {
        if (entries.Count == 0)
        {
            return entries;
        }

        var requests = new List<BotClassificationRequest>(entries.Count);
        var positions = new List<int>(entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (needsClassification is not null && !needsClassification(entry))
            {
                // The 60 s window must see every request, even the ones that
                // already carry flags and will not be classified again.
                _rates.Observe(entry.ClientId ?? "", entry.Path, entry.Timestamp, entry.StatusCode);
                continue;
            }

            requests.Add(BuildRequest(entry));
            positions.Add(i);
        }

        if (requests.Count == 0)
        {
            return entries;
        }

        var verdicts = await _classifier.ClassifyAsync(requests, cancellationToken).ConfigureAwait(false);
        var enriched = new List<RequestLogEntry>(entries);

        for (var j = 0; j < positions.Count && j < verdicts.Count; j++)
        {
            var verdict = verdicts[j];
            if (verdict is null)
            {
                continue;
            }

            int index = positions[j];
            enriched[index] = enriched[index] with
            {
                IsBot = verdict.IsBot,
                IsMaliciousURL = verdict.IsMalicious,
                BotProbability = Math.Round(verdict.Probability, 4),
                BotModel = verdict.ModelVersion,
            };
        }

        return enriched;
    }

    private BotClassificationRequest BuildRequest(RequestLogEntry entry)
    {
        // Old payloads can carry null for fields that are non-nullable in the
        // record; protobuf strings reject null, so normalise here.
        var userAgent = entry.UserAgent ?? "";
        var path = entry.Path ?? "/";
        var countryCode = entry.CountryCode ?? "";
        var method = entry.Method ?? "GET";
        var rates = _rates.Observe(entry.ClientId ?? "", path, entry.Timestamp, entry.StatusCode);

        return new BotClassificationRequest
        {
            RequestId = entry.RequestId ?? entry.Id,
            ClientId = entry.ClientId ?? "",
            RemoteIp = entry.RemoteIp ?? "",
            CountryCode = countryCode,
            Method = method,
            Path = path,
            UserAgent = userAgent,
            UserAgentLength = userAgent.Length,
            PathLength = path.Length,
            UserAgentMissing = userAgent.Length == 0,
            // Python recomputes both flags from the raw path: one source of
            // truth for the label, no duplicated token list here.
            PathIsSensitive = false,
            PathIsExecutable = false,
            PathHasQuery = !string.IsNullOrEmpty(entry.QueryString),
            HasReferer = !string.IsNullOrEmpty(entry.Referer),
            HasCookies = false,
            IsHttps = entry.IsHttps,
            IsHttp2 = entry.Protocol is "HTTP/2" or "HTTP/3",
            RequestsLastMinute = rates.RequestsLastMinute,
            DistinctPathsLastMinute = rates.DistinctPathsLastMinute,
            ErrorsLastMinute = rates.ErrorsLastMinute,
            AuthFailuresLastMinute = rates.AuthFailuresLastMinute,
            SecondsSinceLastRequest = rates.SecondsSinceLastRequest < 0
                ? -1
                : Math.Min(rates.SecondsSinceLastRequest, 3_600),
            UtcHour = entry.Timestamp.UtcDateTime.Hour,
            HttpStatus = entry.StatusCode,
            DurationMs = entry.DurationMs,
            ObservedAtUnixMs = entry.Timestamp.ToUnixTimeMilliseconds(),
        };
    }
}
