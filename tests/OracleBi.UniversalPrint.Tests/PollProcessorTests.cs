using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Polling;
using OracleBi.UniversalPrint.Telemetry;
using Xunit;

namespace OracleBi.UniversalPrint.Tests;

public sealed class PollProcessorTests
{
    private const string PrinterId = "printer-1";
    private const string JobId = "up-job-1";

    private static PollProcessor CreateProcessor(
        IUniversalPrintProvider provider,
        int maxPollAttempts = 5,
        int maxDeliveryAttempts = 5)
    {
        var polling = Options.Create(new PollingOptions
        {
            MaxPollAttempts = maxPollAttempts,
            InitialRepollDelay = TimeSpan.FromSeconds(10),
            MaxRepollDelay = TimeSpan.FromMinutes(5),
        });
        var queue = Options.Create(new QueueOptions { MaxDeliveryAttempts = maxDeliveryAttempts });

        return new PollProcessor(
            provider, polling, queue, new PrintTelemetry(), NullLogger<PollProcessor>.Instance);
    }

    private static PollMessage Message(int pollAttempts = 0) => new()
    {
        CorrelationId = "corr-1",
        PrinterId = PrinterId,
        UniversalPrintJobId = JobId,
        PollAttempts = pollAttempts,
    };

    [Fact]
    public async Task Completed_status_marks_message_done()
    {
        var processor = CreateProcessor(new FakeProvider(PrintJobState.Completed));

        var result = await processor.ProcessAsync(Message(), deliveryAttempt: 1, CancellationToken.None);

        Assert.Equal(PollAction.Completed, result.Action);
    }

    [Fact]
    public async Task Still_printing_reschedules_with_incremented_attempt_and_delay()
    {
        var processor = CreateProcessor(new FakeProvider(PrintJobState.Printing));

        var result = await processor.ProcessAsync(Message(pollAttempts: 1), deliveryAttempt: 1, CancellationToken.None);

        Assert.Equal(PollAction.Reschedule, result.Action);
        Assert.NotNull(result.NextMessage);
        Assert.Equal(2, result.NextMessage!.PollAttempts);
        Assert.True(result.Delay > TimeSpan.Zero);
    }

    [Fact]
    public async Task Failed_status_dead_letters_with_print_job_failed_reason()
    {
        var processor = CreateProcessor(new FakeProvider(PrintJobState.Failed));

        var result = await processor.ProcessAsync(Message(), deliveryAttempt: 1, CancellationToken.None);

        Assert.Equal(PollAction.DeadLetter, result.Action);
        Assert.Equal(DeadLetterReason.PrintJobFailed, result.DeadLetter!.Reason);
    }

    [Fact]
    public async Task Still_printing_past_max_poll_attempts_dead_letters_as_timeout()
    {
        var processor = CreateProcessor(new FakeProvider(PrintJobState.Printing), maxPollAttempts: 3);

        // pollAttempts 2 -> nextAttempt 3 == MaxPollAttempts -> timeout dead-letter.
        var result = await processor.ProcessAsync(Message(pollAttempts: 2), deliveryAttempt: 1, CancellationToken.None);

        Assert.Equal(PollAction.DeadLetter, result.Action);
        Assert.Equal(DeadLetterReason.PollTimeoutExceeded, result.DeadLetter!.Reason);
    }

    [Fact]
    public async Task Exceeding_max_delivery_attempts_dead_letters_without_calling_provider()
    {
        var provider = new FakeProvider(PrintJobState.Completed);
        var processor = CreateProcessor(provider, maxDeliveryAttempts: 5);

        var result = await processor.ProcessAsync(Message(), deliveryAttempt: 6, CancellationToken.None);

        Assert.Equal(PollAction.DeadLetter, result.Action);
        Assert.Equal(DeadLetterReason.MaxDeliveryAttemptsExceeded, result.DeadLetter!.Reason);
        Assert.False(provider.StatusWasRequested);
    }

    private sealed class FakeProvider(PrintJobState state) : IUniversalPrintProvider
    {
        public bool StatusWasRequested { get; private set; }

        public Task<PrintJobStatus> GetStatusAsync(
            string printerId, string universalPrintJobId, CancellationToken cancellationToken = default)
        {
            StatusWasRequested = true;
            return Task.FromResult(new PrintJobStatus
            {
                State = state,
                RawProcessingState = state.ToString().ToLowerInvariant(),
                Description = "test",
            });
        }

        public Task<PrintJob> SubmitAsync(
            OracleBiReportRequest request, string correlationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PrintJob> SubmitDocumentAsync(
            OracleBiReportRequest request, OracleBiDocument document, string correlationId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
