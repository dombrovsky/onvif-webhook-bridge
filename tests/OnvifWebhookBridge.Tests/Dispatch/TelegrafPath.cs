namespace OnvifWebhookBridge.Tests.Dispatch
{
    using System.Diagnostics.Metrics;
    using System.Globalization;
    using Microsoft.Extensions.DependencyInjection;
    using OnvifWebhookBridge.Dispatch;
    using OpenTelemetry.Exporter;
    using OpenTelemetry.Metrics;

    internal static class TelegrafPath
    {
        internal const string Image = "telegraf:1.40.2@sha256:3992a56245a9477dfad5b98d9354d3ea7c1bced0aab6630ba49f23ac9a4d269e";

        internal static async Task Verify(Uri collector, Uri scrape, CancellationToken token)
        {
            using var environment = new MetricsEnvironment();
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            // Wait on actual collector readiness, then on decoded exported values.
            await ReadUntil(client, scrape, _ => true, token);
            foreach (var signalSpecific in new[] { false, true })
            {
                Environment.SetEnvironmentVariable("ONVIF_METRICS_ENABLED", "true");
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", signalSpecific ? "http/protobuf" : "grpc");
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", signalSpecific ? "http://unused.invalid:4318" : collector.AbsoluteUri);
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_PROTOCOL", signalSpecific ? "grpc" : null);
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", signalSpecific ? collector.AbsoluteUri : null);
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_HEADERS", "X-Synthetic-Key=synthetic-secret-DO-NOT-EXPORT");
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddCameraPipeline();
                services.AddBridgeMetrics();
                OtlpExporterOptions? configured = null;
                services.Configure<OtlpExporterOptions>(options => configured = options);
                await using var container = services.BuildServiceProvider();
                container.InitializeBridgeMetrics();
                var provider = container.GetRequiredService<MeterProvider>();
                Require(configured?.Protocol == OtlpExportProtocol.Grpc);
                var expected = signalSpecific ? 5 : 3;
                using var checkpoints = new Meter(BridgeMetrics.MeterName);
                var checkpoint = checkpoints.CreateGauge<long>("bridge.test.checkpoint", "{export}");
                checkpoint.Record(expected * 10 + 1);
                BridgeMetrics.Events.Add(expected);
                BridgeMetrics.Complete(ExecutionResult.Accepted, TimeSpan.FromSeconds(0.1));
                using var standard = new Meter("System.Net.Http");
                standard.CreateHistogram<double>("http.client.request.duration", "s").Record(0.2,
                    new KeyValuePair<string, object?>("server.address", "synthetic-secret-DO-NOT-EXPORT"),
                    new KeyValuePair<string, object?>("error.type", "synthetic-secret-DO-NOT-EXPORT"));
                Require(provider.ForceFlush(15_000));
                var output = await ReadUntil(client, scrape, text => HasValue(text, "bridge_events", expected) &&
                    HasValue(text, "bridge_test_checkpoint", expected * 10 + 1), token);
                Require(output.Contains("onvif-webhook-bridge", StringComparison.Ordinal));
                Require(!output.Contains("synthetic-secret", StringComparison.Ordinal));
                Require(output.Contains("bridge_dispatch_duration", StringComparison.Ordinal));
                Require(output.Contains("le=\"0.005\"", StringComparison.Ordinal));
                // A second export must retain the cumulative value, not add it again.
                checkpoint.Record(expected * 10 + 2);
                Require(provider.ForceFlush(15_000));
                output = await ReadUntil(client, scrape, text => HasValue(text, "bridge_events", expected) &&
                    HasValue(text, "bridge_test_checkpoint", expected * 10 + 2), token);
                Require(!output.Contains("synthetic-secret", StringComparison.Ordinal));
                var pipeline = container.GetRequiredService<CameraPipeline>();
                pipeline.SetSelfOrigins([collector]);
                Require(!provider.ForceFlush(15_000));
                pipeline.SetSelfOrigins([]);
            }
        }

        private static bool HasValue(string text, string name, long expected) => text.Split('\n').Any(line =>
            (line.StartsWith(name + "{", StringComparison.Ordinal) || line.StartsWith(name + " ", StringComparison.Ordinal) ||
             line.StartsWith(name + "_total{", StringComparison.Ordinal) || line.StartsWith(name + "_total ", StringComparison.Ordinal)) &&
            double.TryParse(line[(line.LastIndexOf(' ') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value == expected);

        private static async Task<string> ReadUntil(HttpClient client, Uri scrape, Func<string, bool> ready, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                try
                {
                    var text = await client.GetStringAsync(scrape, deadline.Token);
                    if (ready(text))
                    {
                        return text;
                    }
                }
                catch (HttpRequestException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
            }
        }

        private static void Require(bool success)
        {
            if (!success)
            {
                throw new InvalidOperationException("Synthetic Telegraf metrics verification failed.");
            }
        }
    }
}
