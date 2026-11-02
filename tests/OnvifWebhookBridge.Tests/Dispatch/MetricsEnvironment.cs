namespace OnvifWebhookBridge.Tests.Dispatch
{
    // Serialize callers with the Metrics collection; never inherit a real collector.
    internal sealed class MetricsEnvironment : IDisposable
    {
        private static readonly string[] Names = ["ONVIF_METRICS_ENABLED", "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_ENDPOINT",
            "OTEL_EXPORTER_OTLP_HEADERS", "OTEL_EXPORTER_OTLP_TIMEOUT", "OTEL_EXPORTER_OTLP_METRICS_PROTOCOL",
            "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_HEADERS", "OTEL_EXPORTER_OTLP_METRICS_TIMEOUT"];
        private readonly Dictionary<string, string?> previous = Names.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        internal MetricsEnvironment()
        {
            foreach (var name in Names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
