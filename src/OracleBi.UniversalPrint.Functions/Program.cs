using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OracleBi.UniversalPrint.DependencyInjection;
using OracleBi.UniversalPrint.Telemetry;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // Core Oracle BI → Universal Print services (provider, queue, DLQ, poll processor, telemetry).
        services.AddOracleBiUniversalPrint(context.Configuration);

        // Application Insights for the isolated worker.
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Export the custom ActivitySource + Meter (incl. DLQ metrics) to Azure Monitor.
        // The exporter reads APPLICATIONINSIGHTS_CONNECTION_STRING from configuration.
        services.AddOpenTelemetry()
            .WithTracing(t => t
                .AddSource(PrintTelemetry.ActivitySourceName)
                .AddAzureMonitorTraceExporter())
            .WithMetrics(m => m
                .AddMeter(PrintTelemetry.MeterName)
                .AddAzureMonitorMetricExporter());
    })
    .Build();

host.Run();
