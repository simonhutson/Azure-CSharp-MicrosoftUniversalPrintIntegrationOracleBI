using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Idempotency;
using OracleBi.UniversalPrint.OracleBiIntegration;
using OracleBi.UniversalPrint.Polling;
using OracleBi.UniversalPrint.Queueing;
using OracleBi.UniversalPrint.Telemetry;
using OracleBi.UniversalPrint.UniversalPrintIntegration;

namespace OracleBi.UniversalPrint.DependencyInjection;

/// <summary>
/// Registration helpers for the Oracle BI → Universal Print provider, its queue/DLQ integration,
/// telemetry and (optionally) the in-process polling worker.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core services: options, telemetry, Oracle BI client, Universal Print
    /// provider, queue + dead-letter queue, and the poll processor / submit service.
    /// Call <see cref="AddPrintPollingWorker"/> as well to host polling in-process.
    /// </summary>
    public static IServiceCollection AddOracleBiUniversalPrint(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OracleBiOptions>()
            .Bind(configuration.GetSection(OracleBiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<UniversalPrintOptions>()
            .Bind(configuration.GetSection(UniversalPrintOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PollingOptions>()
            .Bind(configuration.GetSection(PollingOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddOptions<QueueOptions>()
            .Bind(configuration.GetSection(QueueOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PrintSecurityOptions>()
            .Bind(configuration.GetSection(PrintSecurityOptions.SectionName));

        services.AddSingleton<PrintTelemetry>();

        services.AddHttpClient<IOracleBiClient, OracleBiClient>();
        services.AddHttpClient<IUniversalPrintProvider, UniversalPrintProvider>();

        services.AddSingleton<IPrintJobQueue, AzureStoragePrintJobQueue>();
        services.AddSingleton<IDeadLetterQueue, AzureStorageDeadLetterQueue>();
        services.AddSingleton<IIdempotencyStore, BlobIdempotencyStore>();

        services.AddSingleton<PollProcessor>();
        services.AddSingleton<SubmitProcessor>();
        services.AddSingleton<PrintJobService>();

        return services;
    }

    /// <summary>Hosts the background polling worker in this process.</summary>
    public static IServiceCollection AddPrintPollingWorker(this IServiceCollection services)
    {
        services.AddHostedService<PrintJobPollingWorker>();
        return services;
    }
}
