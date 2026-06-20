using System.Net;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace OracleBi.UniversalPrint.Resilience;

/// <summary>
/// Shared resilience pipelines (Polly v8). Used for outbound calls to Oracle BI and Microsoft
/// Graph as well as for Azure Storage Queue operations.
///
/// Best practices baked in here:
///  - Retry only transient failures (5xx, 408, 429, timeouts, socket errors).
///  - Exponential back-off WITH jitter to avoid thundering-herd / synchronised retries.
///  - Honour Retry-After on HTTP 429 / 503 responses when present.
///  - Cap total attempts so a permanently failing dependency is surfaced (and dead-lettered)
///    rather than retried forever.
/// </summary>
public static class ResiliencePipelines
{
    /// <summary>Status codes we treat as transient and therefore retryable.</summary>
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
    [
        HttpStatusCode.RequestTimeout,        // 408
        HttpStatusCode.TooManyRequests,       // 429
        HttpStatusCode.InternalServerError,   // 500
        HttpStatusCode.BadGateway,            // 502
        HttpStatusCode.ServiceUnavailable,    // 503
        HttpStatusCode.GatewayTimeout,        // 504
    ];

    public static bool IsTransientHttp(HttpResponseMessage response) =>
        RetryableStatusCodes.Contains(response.StatusCode);

    /// <summary>
    /// Builds a retry pipeline for raw <see cref="HttpResponseMessage"/> calls (Oracle BI, Graph).
    /// </summary>
    public static ResiliencePipeline<HttpResponseMessage> CreateHttpPipeline(ILogger logger, int maxRetries = 5)
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = maxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(30),
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is HttpRequestException or TimeoutException ||
                    (args.Outcome.Result is { } r && IsTransientHttp(r))),
                DelayGenerator = static args =>
                {
                    // Honour Retry-After when the server tells us how long to wait.
                    if (args.Outcome.Result?.Headers.RetryAfter?.Delta is { } retryAfter)
                    {
                        return ValueTask.FromResult<TimeSpan?>(retryAfter);
                    }

                    return ValueTask.FromResult<TimeSpan?>(null);
                },
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Transient failure on attempt {Attempt}. Retrying in {Delay}. Status={Status}",
                        args.AttemptNumber + 1,
                        args.RetryDelay,
                        args.Outcome.Result?.StatusCode);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    /// <summary>
    /// Builds a retry pipeline for arbitrary async operations (queue I/O) that throw on failure.
    /// </summary>
    public static ResiliencePipeline CreateOperationPipeline(ILogger logger, int maxRetries = 5)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(250),
                MaxDelay = TimeSpan.FromSeconds(15),
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is not null and not OperationCanceledException),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        args.Outcome.Exception,
                        "Transient operation failure on attempt {Attempt}. Retrying in {Delay}.",
                        args.AttemptNumber + 1,
                        args.RetryDelay);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }
}
