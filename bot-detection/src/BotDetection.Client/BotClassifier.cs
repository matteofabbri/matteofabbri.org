using BotDetection.Contracts.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotDetection.Client;

/// <summary>
/// Transport to the Python model. One persistent HTTP/2 channel for the whole
/// process; a batch call per log flush. There is no policy here: on failure the
/// caller gets null verdicts and the request log is written without flags.
/// </summary>
public sealed class BotClassifier : IDisposable
{
    private readonly BotDetectionService.BotDetectionServiceClient _client;
    private readonly GrpcChannel _channel;
    private readonly BotDetectionClientOptions _options;
    private readonly ILogger<BotClassifier> _logger;

    public BotClassifier(
        IOptions<BotDetectionClientOptions> options,
        ILogger<BotClassifier> logger)
    {
        _options = options.Value;
        _logger = logger;

        _channel = GrpcChannel.ForAddress(_options.GrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                ConnectTimeout = TimeSpan.FromSeconds(2)
            },
            MaxReceiveMessageSize = 4 * 1024 * 1024,
            MaxSendMessageSize = 4 * 1024 * 1024
        });

        _client = new BotDetectionService.BotDetectionServiceClient(_channel);
        _logger.LogInformation("Bot detection channel created for {Address}", _options.GrpcUrl);
    }

    /// <summary>
    /// Classifies a batch. Returns one entry per request; a null entry means
    /// "not classified" (disabled, timeout, service unavailable).
    /// </summary>
    public async Task<IReadOnlyList<BotVerdict?>> ClassifyAsync(
        IReadOnlyList<BotClassificationRequest> requests,
        CancellationToken cancellationToken)
    {
        var results = new BotVerdict?[requests.Count];
        if (requests.Count == 0 || !_options.Enabled)
        {
            return results;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMilliseconds));

        try
        {
            var batch = new BotClassificationBatchRequest();
            batch.Requests.AddRange(requests);

            var response = await _client
                .ClassifyBatchAsync(batch, cancellationToken: cts.Token)
                .ConfigureAwait(false);

            for (var i = 0; i < results.Length && i < response.Responses.Count; i++)
            {
                var item = response.Responses[i];
                results[i] = new BotVerdict(
                    item.IsBot,
                    item.IsMalicious,
                    item.Probability,
                    item.ModelVersion);
            }

            return results;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(
                "Bot detection unavailable ({Status}); writing the request log without flags",
                ex.StatusCode);
            return results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Bot detection timed out after {Timeout} ms; writing the request log without flags",
                _options.TimeoutMilliseconds);
            return results;
        }
    }

    public void Dispose() => _channel.Dispose();
}
