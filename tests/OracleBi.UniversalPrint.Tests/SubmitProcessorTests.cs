using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Polling;
using OracleBi.UniversalPrint.Telemetry;
using Xunit;

namespace OracleBi.UniversalPrint.Tests;

public sealed class SubmitProcessorTests
{
    private const string ReportPath = "/Finance/Invoice.xdo";

    private static SubmitMessage Message(string reportPath = ReportPath) => new()
    {
        CorrelationId = "corr-1",
        IdempotencyKey = "key-1",
        Request = new OracleBiReportRequest { ReportPath = reportPath, PrinterId = "printer-1" },
    };

    private static IdempotencyRecord CommittedRecord() => new()
    {
        CorrelationId = "corr-1",
        UniversalPrintJobId = "up-1",
        PrinterId = "printer-1",
        CommittedAt = DateTimeOffset.UtcNow,
    };

    private static (SubmitProcessor Processor, FakeQueue Queue, FakeIdempotencyStore Store, FakeProvider Provider) Build(
        bool claim = true,
        Func<OracleBiReportRequest, string, PrintJob>? onSubmit = null,
        string[]? allowedPrefixes = null,
        int maxDeliveryAttempts = 5,
        IdempotencyRecord? existingRecord = null,
        bool commitThrows = false)
    {
        var provider = new FakeProvider(onSubmit ?? DefaultSubmit);
        var queue = new FakeQueue();
        var store = new FakeIdempotencyStore(claim, existingRecord, commitThrows);

        var polling = Options.Create(new PollingOptions
        {
            MaxPollAttempts = 5,
            InitialRepollDelay = TimeSpan.FromSeconds(10),
            MaxRepollDelay = TimeSpan.FromMinutes(5),
        });
        var security = Options.Create(new PrintSecurityOptions
        {
            AllowedReportPathPrefixes = allowedPrefixes ?? Array.Empty<string>(),
        });
        var printJobService = new PrintJobService(
            provider, queue, polling, security, NullLogger<PrintJobService>.Instance);

        var queueOptions = Options.Create(new QueueOptions { MaxDeliveryAttempts = maxDeliveryAttempts });
        var processor = new SubmitProcessor(
            printJobService, store, queueOptions, new PrintTelemetry(), NullLogger<SubmitProcessor>.Instance);

        return (processor, queue, store, provider);
    }

    private static PrintJob DefaultSubmit(OracleBiReportRequest request, string correlationId) => new()
    {
        CorrelationId = correlationId,
        Request = request,
        PrinterId = "printer-1",
        UniversalPrintJobId = "up-1",
        State = PrintJobState.Printing,
    };

    [Fact]
    public async Task Happy_path_claims_submits_commits_and_schedules_poll()
    {
        var (processor, queue, store, provider) = Build();

        var result = await processor.ProcessAsync(Message(), deliveryAttempt: 1, CancellationToken.None);

        Assert.Equal(SubmitAction.Submitted, result.Action);
        Assert.NotNull(result.Job);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(1, store.CommitCount);
        Assert.Equal(0, store.ReleaseCount);
        Assert.Single(queue.Polls);
        // The commit marker must carry the created job id so a redelivery can recover.
        Assert.Equal("up-1", store.Committed!.UniversalPrintJobId);
        Assert.True(store.Committed!.IsCommitted);
    }

    [Fact]
    public async Task Duplicate_of_committed_claim_redrives_poll_without_resubmitting()
    {
        var (processor, queue, store, provider) = Build(claim: false, existingRecord: CommittedRecord());

        var result = await processor.ProcessAsync(Message(), deliveryAttempt: 1, CancellationToken.None);

        Assert.Equal(SubmitAction.Duplicate, result.Action);
        Assert.Equal(0, provider.SubmitCount);           // never re-submit a committed job
        Assert.Single(queue.Polls);                      // tracking is (re-)driven
        Assert.Equal(0, store.ReleaseCount);             // committed claim is never released
        Assert.Equal("up-1", queue.Polls[0].UniversalPrintJobId);
    }

    [Fact]
    public async Task Duplicate_of_uncommitted_claim_defers_to_retry()
    {
        // Claim held but no commit marker yet (in-flight / crashed before commit).
        var (processor, queue, store, provider) = Build(claim: false, existingRecord: new IdempotencyRecord());

        await Assert.ThrowsAsync<SubmitClaimPendingException>(
            () => processor.ProcessAsync(Message(), deliveryAttempt: 1, CancellationToken.None));

        Assert.Equal(0, provider.SubmitCount);
        Assert.Empty(queue.Polls);
        Assert.Equal(0, store.ReleaseCount);
    }

    [Fact]
    public async Task Exceeding_max_delivery_attempts_dead_letters_without_claiming()
    {
        var (processor, queue, store, provider) = Build(maxDeliveryAttempts: 5);

        var result = await processor.ProcessAsync(Message(), deliveryAttempt: 6, CancellationToken.None);

        Assert.Equal(SubmitAction.DeadLetter, result.Action);
        Assert.Equal(DeadLetterReason.MaxDeliveryAttemptsExceeded, result.DeadLetter!.Reason);
        Assert.Equal(0, store.ClaimCount);
        Assert.Equal(0, provider.SubmitCount);
    }

    [Fact]
    public async Task Disallowed_report_path_dead_letters_as_rejected_and_keeps_claim()
    {
        var (processor, queue, store, provider) = Build(allowedPrefixes: new[] { "/Allowed/" });

        var result = await processor.ProcessAsync(
            Message("/Secret/Report.xdo"), deliveryAttempt: 1, CancellationToken.None);

        Assert.Equal(SubmitAction.DeadLetter, result.Action);
        Assert.Equal(DeadLetterReason.SubmitRejected, result.DeadLetter!.Reason);
        Assert.Equal(0, provider.SubmitCount);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(0, store.CommitCount);
        Assert.Equal(0, store.ReleaseCount);
        Assert.Empty(queue.Polls);
    }

    [Fact]
    public async Task Pre_submit_failure_releases_claim_and_rethrows()
    {
        var (processor, queue, store, provider) = Build(
            onSubmit: (_, _) => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(Message(), deliveryAttempt: 1, CancellationToken.None));

        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(0, store.CommitCount);
        Assert.Equal(1, store.ReleaseCount);   // safe to release: no job was created
        Assert.Empty(queue.Polls);
    }

    [Fact]
    public async Task Failure_after_job_created_keeps_claim_to_prevent_duplicate()
    {
        // The job is created (provider returns), but committing the marker fails. The claim must
        // NOT be released, otherwise a retry would create a duplicate print.
        var (processor, queue, store, provider) = Build(commitThrows: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(Message(), deliveryAttempt: 1, CancellationToken.None));

        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(0, store.ReleaseCount);   // claim retained -> no duplicate on retry
    }

    private sealed class FakeProvider(Func<OracleBiReportRequest, string, PrintJob> onSubmit)
        : IUniversalPrintProvider
    {
        public int SubmitCount { get; private set; }

        public Task<PrintJob> SubmitAsync(
            OracleBiReportRequest request, string correlationId, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(onSubmit(request, correlationId));
        }

        public Task<PrintJob> SubmitDocumentAsync(
            OracleBiReportRequest request, OracleBiDocument document, string correlationId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PrintJobStatus> GetStatusAsync(
            string printerId, string universalPrintJobId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeQueue : IPrintJobQueue
    {
        public List<PollMessage> Polls { get; } = new();
        public List<SubmitMessage> Submits { get; } = new();

        public Task EnqueueSubmitAsync(SubmitMessage message, CancellationToken cancellationToken = default)
        {
            Submits.Add(message);
            return Task.CompletedTask;
        }

        public Task EnqueuePollAsync(PollMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Polls.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QueuedPollMessage>> ReceiveAsync(
            int maxMessages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CompleteAsync(QueuedPollMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AbandonAsync(QueuedPollMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeIdempotencyStore(
        bool claimResult, IdempotencyRecord? record, bool commitThrows) : IIdempotencyStore
    {
        private IdempotencyRecord? _record = record;

        public int ClaimCount { get; private set; }
        public int CommitCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public int GetCount { get; private set; }
        public IdempotencyRecord? Committed { get; private set; }

        public Task<bool> TryClaimAsync(string idempotencyKey, CancellationToken cancellationToken = default)
        {
            ClaimCount++;
            return Task.FromResult(claimResult);
        }

        public Task CommitAsync(string idempotencyKey, IdempotencyRecord record, CancellationToken cancellationToken = default)
        {
            CommitCount++;
            if (commitThrows)
            {
                throw new InvalidOperationException("commit failed");
            }

            Committed = record;
            _record = record;
            return Task.CompletedTask;
        }

        public Task<IdempotencyRecord?> GetAsync(string idempotencyKey, CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(_record);
        }

        public Task ReleaseAsync(string idempotencyKey, CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            _record = null;
            return Task.CompletedTask;
        }
    }
}
