using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Functions;

/// <summary>
/// Reacts to dead-letter queue events for near-real-time notification. This is one of several
/// notification options (see README): it pushes an Adaptive Card / webhook payload to a Teams or
/// Slack incoming webhook (or a Logic App). Because it triggers on the DLQ itself, alerting is
/// immediate rather than waiting for a metric-alert evaluation window.
///
/// The envelope carries the print job <see cref="DeadLetterEnvelope.CorrelationId"/>, so the
/// notification links straight back to the originating job in Application Insights.
/// </summary>
public sealed class DeadLetterMonitorFunction
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DeadLetterMonitorFunction> _logger;

    public DeadLetterMonitorFunction(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<DeadLetterMonitorFunction> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    [Function(nameof(DeadLetterMonitorFunction))]
    public async Task RunAsync(
        [QueueTrigger("%Queues:DeadLetterQueueName%", Connection = "QueueStorage")] string messageText,
        CancellationToken cancellationToken)
    {
        DeadLetterEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(messageText);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Dead-letter monitor could not parse envelope; leaving message for inspection.");
            return;
        }

        if (envelope is null)
        {
            return;
        }

        _logger.LogCritical(
            "ALERT: print job dead-lettered. CorrelationId={CorrelationId} Reason={Reason} Printer={Printer}",
            envelope.CorrelationId, envelope.ReasonCode, envelope.PrinterId);

        var webhookUrl = _configuration["Notifications:WebhookUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            _logger.LogInformation("No Notifications:WebhookUrl configured; skipping outbound notification.");
            return;
        }

        var card = BuildAdaptiveCard(envelope);
        var client = _httpClientFactory.CreateClient(nameof(DeadLetterMonitorFunction));

        using var response = await client.PostAsJsonAsync(webhookUrl, card, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Throw so the runtime retries the notification (the alert itself must be reliable).
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"DLQ notification webhook failed: {(int)response.StatusCode} {body}");
        }
    }

    private static object BuildAdaptiveCard(DeadLetterEnvelope e) => new
    {
        type = "message",
        attachments = new[]
        {
            new
            {
                contentType = "application/vnd.microsoft.card.adaptive",
                content = new
                {
                    type = "AdaptiveCard",
                    version = "1.4",
                    body = new object[]
                    {
                        new { type = "TextBlock", size = "Large", weight = "Bolder",
                              text = "🛑 Universal Print job dead-lettered" },
                        new { type = "FactSet", facts = new object[]
                            {
                                new { title = "Correlation Id", value = e.CorrelationId },
                                new { title = "Reason", value = e.ReasonCode },
                                new { title = "Printer", value = e.PrinterId ?? "unknown" },
                                new { title = "UP Job Id", value = e.UniversalPrintJobId ?? "n/a" },
                                new { title = "Delivery attempts", value = e.DeliveryAttempts.ToString() },
                                new { title = "When (UTC)", value = e.DeadLetteredAt.ToString("u") },
                            }
                        },
                        // Error detail is intentionally NOT included: it can carry sensitive content.
                        // Look up the correlation id in Application Insights for the full diagnostics.
                        new { type = "TextBlock", isSubtle = true, wrap = true,
                              text = "Use the correlation id in Application Insights for full detail." },
                    },
                },
            },
        },
    };
}
