using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Resilience;
using Polly;

namespace OracleBi.UniversalPrint.Idempotency;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IIdempotencyStore"/>. A claim is an atomic
/// "create blob only if it does not already exist" (If-None-Match: *). The first caller wins;
/// concurrent or redelivered callers get a 409 and are told the submit is a duplicate.
///
/// The blob is two-phase: it starts empty (the reservation) and is overwritten with a JSON
/// <see cref="IdempotencyRecord"/> once the Universal Print job is created (the commit marker).
/// A committed marker is never released, so a retry can recover tracking but can never re-submit
/// and create a duplicate print. An empty (uncommitted) claim left behind by a crash is bounded by
/// the submit queue's visibility timeout and ultimately surfaced via a max-delivery dead-letter.
/// </summary>
public sealed class BlobIdempotencyStore : IIdempotencyStore
{
    private readonly BlobContainerClient _container;
    private readonly ResiliencePipeline _pipeline;

    public BlobIdempotencyStore(IOptions<QueueOptions> options, ILogger<BlobIdempotencyStore> logger)
    {
        _container = BlobContainerClientFactory.Create(options.Value);
        _container.CreateIfNotExists();
        _pipeline = ResiliencePipelines.CreateOperationPipeline(logger);
    }

    public async Task<bool> TryClaimAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(BlobName(idempotencyKey));
        var uploadOptions = new BlobUploadOptions
        {
            // If-None-Match: * => succeed only when the blob does not already exist (atomic claim).
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            Metadata = new Dictionary<string, string>
            {
                ["claimedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            },
        };

        // Run through the retry pipeline for transient storage faults, but treat a 409 (already
        // claimed) as a definitive "duplicate" result rather than a retryable failure.
        return await _pipeline.ExecuteAsync(
            async ct =>
            {
                try
                {
                    await blob.UploadAsync(BinaryData.FromBytes(ReadOnlyMemory<byte>.Empty), uploadOptions, ct);
                    return true;
                }
                catch (RequestFailedException ex) when (ex.Status == 409)
                {
                    return false;
                }
            },
            cancellationToken);
    }

    public async Task CommitAsync(
        string idempotencyKey, IdempotencyRecord record, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(BlobName(idempotencyKey));
        var json = JsonSerializer.SerializeToUtf8Bytes(record);

        // We own the claim, so overwrite the reservation blob unconditionally with the commit
        // marker. This is the durable "the Universal Print job exists" record.
        await _pipeline.ExecuteAsync(
            async ct => await blob.UploadAsync(BinaryData.FromBytes(json), overwrite: true, ct),
            cancellationToken);
    }

    public async Task<IdempotencyRecord?> GetAsync(
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(BlobName(idempotencyKey));
        return await _pipeline.ExecuteAsync(
            async ct =>
            {
                try
                {
                    var response = await blob.DownloadContentAsync(ct);
                    var content = response.Value.Content;
                    if (content is null || content.ToMemory().Length == 0)
                    {
                        // Reservation exists but no commit marker yet (claimed, not committed).
                        return new IdempotencyRecord();
                    }

                    return JsonSerializer.Deserialize<IdempotencyRecord>(content.ToMemory().Span);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Not claimed (or already released).
                    return null;
                }
            },
            cancellationToken);
    }

    public async Task ReleaseAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(BlobName(idempotencyKey));
        await _pipeline.ExecuteAsync(
            async ct => await blob.DeleteIfExistsAsync(cancellationToken: ct),
            cancellationToken);
    }

    /// <summary>
    /// Hashes the (possibly client-supplied) idempotency key to a fixed, always-valid blob name so
    /// arbitrary key values cannot violate blob naming rules.
    /// </summary>
    private static string BlobName(string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>Builds the idempotency <see cref="BlobContainerClient"/> via connection string or managed identity.</summary>
internal static class BlobContainerClientFactory
{
    public static BlobContainerClient Create(QueueOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new BlobContainerClient(options.ConnectionString, options.IdempotencyContainerName);
        }

        if (!string.IsNullOrWhiteSpace(options.BlobServiceUri))
        {
            var uri = new Uri($"{options.BlobServiceUri.TrimEnd('/')}/{options.IdempotencyContainerName}");
            return new BlobContainerClient(uri, new DefaultAzureCredential());
        }

        throw new InvalidOperationException(
            "QueueOptions requires either ConnectionString or BlobServiceUri to be set for idempotency.");
    }
}
