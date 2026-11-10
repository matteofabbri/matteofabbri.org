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

    /// <summary>
    /// Sends a manual verdict for one client: the model relabels that client's
    /// stored observations (so the next retraining run learns it) and applies
    /// the verdict to its next requests immediately. Returns null when the
    /// model does not answer; the caller keeps the flags it already stored.
    /// </summary>
    public async Task<BotFeedbackResult?> LabelClientAsync(
        string clientId,
        bool? isBot,
        bool? isMalicious,
        string? labelledBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !_options.Enabled)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMilliseconds));

        try
        {
            var request = new LabelClientRequest
            {
                ClientId = clientId,
                LabelledBy = labelledBy ?? "",
            };
            if (isBot is bool bot)
            {
                request.IsBot = bot;
            }
            if (isMalicious is bool malicious)
            {
                request.IsMalicious = malicious;
            }

            var response = await _client
                .LabelClientAsync(request, cancellationToken: cts.Token)
                .ConfigureAwait(false);

            return new BotFeedbackResult(
                response.ObservationsUpdated,
                response.ManualLabelActive,
                response.RetrainRequested);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(
                "Bot detection unavailable for the client label ({Status}); the log flags are already saved",
                ex.StatusCode);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Client label timed out after {Timeout} ms; the log flags are already saved",
                _options.TimeoutMilliseconds);
            return null;
        }
    }

    public void Dispose() => _channel.Dispose();
}