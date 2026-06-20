using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OracleBi.UniversalPrint.DependencyInjection;
using OracleBi.UniversalPrint.Telemetry;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // Core Oracle BI → Universal Print services (provider, queue, DLQ, poll/submit processors, telemetry).
        services.AddOracleBiUniversalPrint(context.Configuration);

        // Single OpenTelemetry pipeline (host.json telemetryMode=OpenTelemetry) exported to Azure Monitor.
        // UseFunctionsWorkerDefaults() wires the Functions host/worker traces+logs; the custom
        // ActivitySource + Meter are added below. UseAzureMonitorExporter() registers the trace,
        // metric, and log exporters from APPLICATIONINSIGHTS_CONNECTION_STRING — replacing the classic
        // Application Insights SDK so signals are exported exactly once.
        services.AddOpenTelemetry()
            .UseFunctionsWorkerDefaults()
            .WithTracing(t => t
                .AddSource(PrintTelemetry.ActivitySourceName)
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddMeter(PrintTelemetry.MeterName))
            .UseAzureMonitorExporter();
    })
    .Build();

host.Run();
