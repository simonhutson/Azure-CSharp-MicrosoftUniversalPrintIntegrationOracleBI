using System.Diagnostics;
using System.Diagnostics.Metrics;
using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Telemetry;

/// <summary>
/// Central telemetry surface for the provider. Exposes a single <see cref="ActivitySource"/>
/// for distributed tracing and a <see cref="Meter"/> with the counters/histograms used to
/// build dashboards and alerts (including dead-letter queue metrics).
///
/// Register the names below with OpenTelemetry so they flow to Application Insights:
///   .WithTracing(t =&gt; t.AddSource(PrintTelemetry.ActivitySourceName))
///   .WithMetrics(m =&gt; m.AddMeter(PrintTelemetry.MeterName))
/// </summary>
public sealed class PrintTelemetry : IDisposable
{
    public const string ActivitySourceName = "OracleBi.UniversalPrint";
    public const string MeterName = "OracleBi.UniversalPrint";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly Meter _meter;

    private readonly Counter<long> _jobsSubmitted;
    private readonly Counter<long> _jobsCompleted;
    private readonly Counter<long> _jobsFailed;
    private readonly Counter<long> _pollAttempts;
    private readonly Counter<long> _deadLettered;
    private readonly Histogram<double> _pollLatencyMs;
    private readonly Histogram<double> _endToEndSeconds;

    public PrintTelemetry()
    {
        _meter = new Meter(MeterName);

        _jobsSubmitted = _meter.CreateCounter<long>(
            "print.jobs.submitted", unit: "{job}", description: "Print jobs submitted to Universal Print.");
        _jobsCompleted = _meter.CreateCounter<long>(
            "print.jobs.completed", unit: "{job}", description: "Print jobs that completed successfully.");
        _jobsFailed = _meter.CreateCounter<long>(
            "print.jobs.failed", unit: "{job}", description: "Print jobs that failed (pre dead-letter).");
        _pollAttempts = _meter.CreateCounter<long>(
            "print.poll.attempts", unit: "{attempt}", description: "Status poll attempts performed.");
        _deadLettered = _meter.CreateCounter<long>(
            "print.deadletter.count", unit: "{message}", description: "Messages written to the dead-letter queue.");
        _pollLatencyMs = _meter.CreateHistogram<double>(
            "print.poll.latency", unit: "ms", description: "Latency of a Universal Print status poll.");
        _endToEndSeconds = _meter.CreateHistogram<double>(
            "print.job.duration", unit: "s", description: "End-to-end time from submit to terminal state.");
    }

    /// <summary>Starts a trace activity, stamping the correlation id so all spans/logs join up.</summary>
    public Activity? StartActivity(string name, string correlationId, ActivityKind kind = ActivityKind.Internal)
    {
        var activity = ActivitySource.StartActivity(name, kind);
        activity?.SetTag("print.correlation_id", correlationId);
        return activity;
    }

    public void JobSubmitted(string printerId) =>
        _jobsSubmitted.Add(1, new KeyValuePair<string, object?>("printer.id", printerId));

    public void JobCompleted(string printerId, TimeSpan duration)
    {
        _jobsCompleted.Add(1, new KeyValuePair<string, object?>("printer.id", printerId));
        _endToEndSeconds.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("printer.id", printerId));
    }

    public void JobFailed(string printerId, string reason) =>
        _jobsFailed.Add(1,
            new KeyValuePair<string, object?>("printer.id", printerId),
            new KeyValuePair<string, object?>("failure.reason", reason));

    public void PollAttempted(string printerId, double latencyMs, PrintJobState resultState)
    {
        _pollAttempts.Add(1,
            new KeyValuePair<string, object?>("printer.id", printerId),
            new KeyValuePair<string, object?>("result.state", resultState.ToString()));
        _pollLatencyMs.Record(latencyMs, new KeyValuePair<string, object?>("printer.id", printerId));
    }

    /// <summary>
    /// Records a dead-letter event. The dimensions (reason, printer) are what dashboards group
    /// by and what metric alerts threshold on.
    /// </summary>
    public void DeadLettered(DeadLetterReason reason, string? printerId) =>
        _deadLettered.Add(1,
            new KeyValuePair<string, object?>("deadletter.reason", reason.ToString()),
            new KeyValuePair<string, object?>("printer.id", printerId ?? "unknown"));

    public void Dispose() => _meter.Dispose();
}
