namespace BotDetection.Client;

public readonly record struct ClientRateSnapshot(
    int RequestsLastMinute,
    int DistinctPathsLastMinute,
    int ErrorsLastMinute,
    int AuthFailuresLastMinute,
    double SecondsSinceLastRequest);

/// <summary>
/// Behavioural features for the model, computed from the log stream itself.
///
/// The request-middleware does not compute anything: the writer feeds every
/// entry here before classification, so the sliding window sees the same
/// requests that are about to be stored. Six 10-second buckets give a true
/// 60-second window; distinct paths use a capped 64-entry hash sketch, because
/// a scanner rotating random URLs must not grow an unbounded set.
/// </summary>
public sealed class ClientRateTracker
{
    private const int BucketCount = 6;
    private const int BucketSeconds = 10;
    private const int PathSketchCapacity = 64;
    private const int MaxClients = 100_000;

    private readonly Dictionary<string, ClientState> _clients = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ClientRateSnapshot Observe(
        string clientId,
        string path,
        DateTimeOffset timestamp,
        int statusCode)
    {
        lock (_gate)
        {
            if (_clients.Count >= MaxClients)
            {
                Sweep(timestamp);
            }

            if (!_clients.TryGetValue(clientId, out var state))
            {
                state = new ClientState();
                _clients[clientId] = state;
            }

            return state.Observe(path, timestamp, statusCode);
        }
    }

    /// <summary>Drops clients idle for more than ten minutes (only at the capacity limit).</summary>
    private void Sweep(DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-10);
        var stale = new List<string>();

        foreach (var pair in _clients)
        {
            if (pair.Value.LastSeen < cutoff)
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var key in stale)
        {
            _clients.Remove(key);
        }
    }

    private sealed class ClientState
    {
        private readonly long[] _bucketStart = new long[BucketCount];
        private readonly int[] _requests = new int[BucketCount];
        private readonly int[] _errors = new int[BucketCount];
        private readonly int[] _authFailures = new int[BucketCount];
        private readonly HashSet<int> _paths = new(PathSketchCapacity);

        private long _pathWindow;
        private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;

        public DateTimeOffset LastSeen => _lastSeen;

        public ClientRateSnapshot Observe(string path, DateTimeOffset timestamp, int statusCode)
        {
            var window = timestamp.ToUnixTimeSeconds() / BucketSeconds;
            var bucket = (int)(window % BucketCount);

            if (_bucketStart[bucket] != window)
            {
                _bucketStart[bucket] = window;
                _requests[bucket] = 0;
                _errors[bucket] = 0;
                _authFailures[bucket] = 0;
            }

            _requests[bucket]++;

            if (statusCode >= 400)
            {
                _errors[bucket]++;
            }

            if (statusCode is 401 or 403)
            {
                _authFailures[bucket]++;
            }

            if (window - _pathWindow >= BucketCount)
            {
                _paths.Clear();
                _pathWindow = window;
            }

            if (_paths.Count < PathSketchCapacity)
            {
                _paths.Add(string.GetHashCode(path.AsSpan()));
            }

            var seconds = _lastSeen == DateTimeOffset.MinValue
                ? -1
                : (timestamp - _lastSeen).TotalSeconds;

            _lastSeen = timestamp;

            var requests = 0;
            var errors = 0;
            var authFailures = 0;
            for (var i = 0; i < BucketCount; i++)
            {
                if (window - _bucketStart[i] < BucketCount)
                {
                    requests += _requests[i];
                    errors += _errors[i];
                    authFailures += _authFailures[i];
                }
            }

            return new ClientRateSnapshot(requests, _paths.Count, errors, authFailures, seconds);
        }
    }
}
