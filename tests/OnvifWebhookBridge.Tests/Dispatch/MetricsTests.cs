namespace OnvifWebhookBridge.Tests.Dispatch
{
    using Xunit;
    using OnvifWebhookBridge.Dispatch;
    using OpenTelemetry.Exporter;
    using OpenTelemetry.Metrics;

    [CollectionDefinition("Metrics", DisableParallelization = true)]
    public sealed class MetricsCollection;

    [Collection("Metrics"), Trait("Category", "Unit")]
    public sealed class MetricsTests
    {
        [Fact]
        public Task ExportsCumulativeRedactedMetricsAndSettlesEveryAcceptedJob() => MetricsPath.Verify();

        [Theory]
        [InlineData(null, null, OtlpExportProtocol.HttpProtobuf, 4318)]
        [InlineData("grpc", null, OtlpExportProtocol.Grpc, 4317)]
        [InlineData("http/protobuf", null, OtlpExportProtocol.HttpProtobuf, 4318)]
        [InlineData("grpc", "http/protobuf", OtlpExportProtocol.HttpProtobuf, 4318)]
        [InlineData("http/protobuf", "grpc", OtlpExportProtocol.Grpc, 4317)]
        [InlineData(null, "grpc", OtlpExportProtocol.Grpc, 4317)]
        [InlineData("grpc", " ", OtlpExportProtocol.Grpc, 4317)]
        public void ProtocolPrecedenceAndDefaultPorts(string? generic, string? signal, OtlpExportProtocol expected, int port)
        {
            using var environment = new MetricsEnvironment();
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", generic);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_PROTOCOL", signal);
            var exporter = new OtlpExporterOptions();
            var reader = new MetricReaderOptions();
            BridgeMetrics.ConfigureExporter(exporter, reader);
            Assert.Equal(expected, exporter.Protocol);
            Assert.Equal(port, exporter.Endpoint.Port);
            Assert.Equal(MetricReaderTemporalityPreference.Cumulative, reader.TemporalityPreference);
            Assert.Equal(60_000, reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds);
        }

        [Fact]
        public void MetricsOverridesGenericOptionsAndInvalidValuesAreNotEchoed()
        {
            using var environment = new MetricsEnvironment();
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://generic.invalid:4318");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", "X-Test=generic");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TIMEOUT", "1000");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "http://signal.invalid:4317");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_HEADERS", "X-Test=signal");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_TIMEOUT", "2000");
            var exporter = new OtlpExporterOptions();
            BridgeMetrics.ConfigureExporter(exporter, new());
            Assert.Equal("signal.invalid", exporter.Endpoint.Host);
            Assert.Equal("X-Test=signal", exporter.Headers);
            Assert.Equal(2000, exporter.TimeoutMilliseconds);
            foreach (var setting in new[] { "PROTOCOL", "ENDPOINT", "TIMEOUT" })
            {
                var name = "OTEL_EXPORTER_OTLP_METRICS_" + setting;
                var previous = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, "synthetic-secret-DO-NOT-LOG");
                var error = Assert.Throws<ArgumentException>(() => BridgeMetrics.ConfigureExporter(new(), new()));
                Assert.DoesNotContain("synthetic-secret", error.Message);
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }
}
