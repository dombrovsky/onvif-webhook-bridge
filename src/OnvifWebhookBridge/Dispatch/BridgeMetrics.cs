namespace OnvifWebhookBridge.Dispatch
{
    using System.Diagnostics.Metrics;
    using OnvifWebhookBridge.Onvif;
    using OpenTelemetry;
    using OpenTelemetry.Exporter;
    using OpenTelemetry.Metrics;
    using OpenTelemetry.Resources;

    internal static class BridgeMetrics
    {
        internal const string MeterName = "OnvifWebhookBridge";
        internal static readonly double[] DurationBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 20, 30, 60];
        private static readonly Meter Meter = new(MeterName);
        internal static readonly Counter<long> Events = Meter.CreateCounter<long>("bridge.events", "{event}");
        internal static readonly Counter<long> Decisions = Meter.CreateCounter<long>("bridge.rule.decisions", "{decision}");
        internal static readonly Counter<long> SubscriptionFailures = Meter.CreateCounter<long>("bridge.subscription.failures", "{failure}");
        internal static readonly Counter<long> Admissions = Meter.CreateCounter<long>("bridge.dispatch.admissions", "{job}");
        internal static readonly Counter<long> Retries = Meter.CreateCounter<long>("bridge.dispatch.retries", "{retry}");
        private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("bridge.dispatch.outcomes", "{job}");
        private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("bridge.dispatch.duration", "s");

        internal static void Complete(ExecutionResult result, TimeSpan elapsed)
        {
            Outcomes.Add(1, new KeyValuePair<string, object?>("result", result.ToString()));
            Latency.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("result", result.ToString()));
        }

        internal static IServiceCollection AddBridgeMetrics(this IServiceCollection services)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("ONVIF_METRICS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return services;
            }
            services.AddSingleton<CameraMeasurements>();
            services.AddOptions<OtlpExporterOptions>().Configure<CameraMeasurements>((options, cameras) =>
                options.HttpClientFactory = () => new HttpClient(new CollectorHandler(cameras))
                {
                    Timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds)
                });
            services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics.ConfigureBridgeMetrics().AddOtlpExporter(ConfigureExporter).AddMeter("OnvifWebhookBridge.Cameras")
                .AddInstrumentation(provider => provider.GetRequiredService<CameraMeasurements>()));
            return services;
        }

        internal static void InitializeBridgeMetrics(this IServiceProvider services)
        {
            if (services.GetService<CameraMeasurements>() is { } cameras)
            {
                cameras.Supervisor = services.GetRequiredService<IEnumerable<IHostedService>>().OfType<CameraSupervisor>().Single();
                cameras.Pipeline = services.GetRequiredService<CameraPipeline>();
            }
        }

        internal static void ConfigureExporter(OtlpExporterOptions exporter, MetricReaderOptions reader)
        {
            // HTTP/protobuf is supported by NativeAOT; no traces or log providers.
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
            reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 60_000;
        }

        internal static MeterProviderBuilder ConfigureBridgeMetrics(this MeterProviderBuilder metrics)
            => metrics.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("onvif-webhook-bridge"))
                .SetExemplarFilter(ExemplarFilterType.AlwaysOff)
                .AddMeter(MeterName, "System.Runtime", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel",
                    "System.Net.Http", "System.Net.NameResolution")
                .AddView(instrument =>
                {
                    // Apply to every subscribed instrument, including future framework metrics.
                    // Never export host/address, route, topic, identity or error text tags.
                    string[] tags = instrument.Meter.Name.StartsWith("OnvifWebhookBridge", StringComparison.Ordinal)
                        ? ["result", "decision", "state"]
                        : ["http.request.method", "http.response.status_code", "network.protocol.version",
                            "cpu.mode", "gc.heap.generation", "http.connection.state"];
                    return instrument is Histogram<double> or Histogram<long> or Histogram<int>
                        ? new ExplicitBucketHistogramConfiguration
                        {
                            TagKeys = tags,
                            Boundaries = instrument.Unit == "s" ? DurationBuckets : [0, 1, 10, 100, 1000, 10000, 100000, 1000000, 10000000, 100000000],
                            RecordMinMax = false
                        }
                        : new MetricStreamConfiguration { TagKeys = tags };
                });

        private sealed class CameraMeasurements : IDisposable
        {
            private readonly Meter meter = new("OnvifWebhookBridge.Cameras");
            internal CameraSupervisor? Supervisor
            {
                get; set;
            }
            internal CameraPipeline? Pipeline
            {
                get; set;
            }
            private static readonly string[] States = ["Disabled", "Connecting", "Subscribed", "Reconnecting", "Error", "Stopped", "Unknown"];
            public CameraMeasurements(TimeProvider clock)
            {
                meter.CreateObservableGauge("bridge.camera.count", () =>
                {
                    var counts = (Supervisor?.Status ?? []).GroupBy(s => SafeState(s.State)).ToDictionary(group => group.Key, group => group.Count());
                    return States.Select(state => new Measurement<int>(counts.GetValueOrDefault(state),
                        new KeyValuePair<string, object?>("state", state)));
                }, "{camera}");
                meter.CreateObservableGauge("bridge.camera.contact.age", () => (Supervisor?.Status ?? []).Where(s => s.LastContact.HasValue)
                    .Select(s => Math.Max(0, (clock.GetUtcNow() - s.LastContact!.Value).TotalSeconds))
                    .DefaultIfEmpty(0).Max(), "s", "Maximum contact age among cameras with successful contact.");
                meter.CreateObservableGauge("bridge.camera.contact.unknown", () => (Supervisor?.Status ?? []).Count(s => !s.LastContact.HasValue), "{camera}");
            }
            private static string SafeState(string state) => state is "Disabled" or "Connecting" or "Subscribed" or "Reconnecting" or "Error" or "Stopped"
                ? state : "Unknown";
            public void Dispose() => meter.Dispose();
        }

        private sealed class CollectorHandler(CameraMeasurements cameras) : DelegatingHandler(HttpActionDispatcher.CreateHandler())
        {
            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
                => SendAsync(request, cancellationToken).GetAwaiter().GetResult();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (cameras.Pipeline is not { } pipeline || request.RequestUri is not { } address ||
                    address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0 || address.Fragment.Length != 0 ||
                    pipeline.IsSelfAddress(address))
                {
                    throw new HttpRequestException("Blocked collector.");
                }
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
