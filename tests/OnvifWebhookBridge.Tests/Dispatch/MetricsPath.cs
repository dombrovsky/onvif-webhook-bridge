namespace OnvifWebhookBridge.Tests.Dispatch
{
    using System.Diagnostics.Metrics;
    using System.Net;
    using System.Net.Http.Headers;
    using System.Text;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Rules;
    using OpenTelemetry;
    using OpenTelemetry.Exporter;
    using OpenTelemetry.Metrics;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    internal static class MetricsPath
    {
        internal static async Task Verify()
        {
            const string secret = "synthetic-secret-DO-NOT-EXPORT";
            using var capture = new Capture();
            using var reader = new PeriodicExportingMetricReader(capture, 60_000)
            {
                TemporalityPreference = MetricReaderTemporalityPreference.Cumulative
            };
            using var wire = new Wire();
            using var provider = Sdk.CreateMeterProviderBuilder().ConfigureBridgeMetrics()
                .AddReader(reader)
                .AddOtlpExporter((options, settings) =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Endpoint = new Uri("http://collector.invalid/v1/metrics");
                    options.HttpClientFactory = () => new HttpClient(wire, false);
                    settings.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                    settings.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 60_000;
                }).Build();
            using var standard = new Meter("System.Net.Http");
            standard.CreateHistogram<double>("http.client.request.duration", "s").Record(0.1,
                new KeyValuePair<string, object?>("server.address", secret),
                new KeyValuePair<string, object?>("error.type", secret));

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var blocked = new Factory(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(HttpStatusCode.OK);
            });
            await using (var dispatcher = new HttpActionDispatcher(blocked, TimeProvider.System))
            {
                var pipeline = Pipeline(dispatcher, secret);
                Require(pipeline.ManualTest(secret));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var cancellation = new CancellationTokenSource();
                for (var i = 0; i < 25; i++)
                {
                    Require(pipeline.ManualTest(secret, cancellation.Token));
                }
                Require(!pipeline.ManualTest(secret));
                cancellation.Cancel();
            }
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Name == "bridge.dispatch.outcomes")
                {
                    current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (Equals(tag.Value, "Accepted"))
                    {
                        done.TrySetResult();
                    }
                }
            });
            listener.Start();
            var attempts = 0;
            using var retry = new Factory((_, _) =>
            {
                var response = new HttpResponseMessage(++attempts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            });
            await using (var dispatcher = new HttpActionDispatcher(retry, TimeProvider.System))
            {
                var pipeline = Pipeline(dispatcher, secret, 1);
                Require(pipeline.ManualTest(secret));
                await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var decisions = pipeline.Process(new(secret, secret, DateTimeOffset.UtcNow, "unmatched-topic", null,
                    [], [], null, null, null, [], [], [], secret, false, [], 0));
                Require(decisions.Single().Kind == DecisionKind.Unmatched);
            }
            Require(provider.ForceFlush());
            Require(capture.SafeTags);
            Require(capture.ValidBuckets);
            Require(capture.Values["bridge.dispatch.outcomes:Cancelled"] == 26);
            Require(capture.Values["bridge.dispatch.outcomes:Accepted"] == 1);
            Require(capture.Values["bridge.dispatch.admissions:Accepted"] == 27);
            Require(capture.Values["bridge.dispatch.admissions:Rejected"] == 1);
            Require(capture.Values["bridge.dispatch.retries:"] == 1);
            Require(capture.Values["bridge.events:"] == 1);
            Require(capture.Values["bridge.rule.decisions:"] == 1);
            Require(capture.HistogramCount == 27);
            Require(provider.ForceFlush());
            Require(capture.Values["bridge.dispatch.outcomes:Cancelled"] == 26);
            Require(!Encoding.UTF8.GetString(wire.Payload).Contains(secret, StringComparison.Ordinal));
            Require(Encoding.UTF8.GetString(wire.Payload).Contains("onvif-webhook-bridge", StringComparison.Ordinal));
            await VerifyRegistration();
        }

        private static async Task VerifyRegistration()
        {
            var previous = Environment.GetEnvironmentVariable("ONVIF_METRICS_ENABLED");
            try
            {
                Environment.SetEnvironmentVariable("ONVIF_METRICS_ENABLED", null);
                var disabled = new ServiceCollection();
                disabled.AddBridgeMetrics();
                Require(!disabled.Any(s => s.ServiceType == typeof(MeterProvider)));
                Environment.SetEnvironmentVariable("ONVIF_METRICS_ENABLED", "true");
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddCameraPipeline();
                services.AddBridgeMetrics();
                using var wire = new Wire();
                using var finalCapture = new Capture();
                using var finalReader = new PeriodicExportingMetricReader(finalCapture, 60_000);
                services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(finalReader));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var transport = new Factory(async (_, token) =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    throw new OperationCanceledException(token);
                });
                services.AddSingleton<IHttpClientFactory>(transport);
                Func<HttpClient>? collectorFactory = null;
                services.Configure<OtlpExporterOptions>(options =>
                {
                    collectorFactory = options.HttpClientFactory;
                    options.HttpClientFactory = () => new HttpClient(wire, false);
                });
                await using var container = services.BuildServiceProvider();
                container.InitializeBridgeMetrics();
                container.GetRequiredService<CameraPipeline>().Configure(new("synthetic-camera", "Synthetic", "http://camera.invalid/", Enabled: false), []);
                var meterProvider = container.GetRequiredService<MeterProvider>();
                Require(meterProvider.ForceFlush());
                Require(Encoding.UTF8.GetString(wire.Payload).Contains("bridge.camera.count", StringComparison.Ordinal));
                var options = new OtlpExporterOptions { Protocol = OtlpExportProtocol.Grpc };
                var reader = new MetricReaderOptions { TemporalityPreference = MetricReaderTemporalityPreference.Delta };
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1;
                BridgeMetrics.ConfigureExporter(options, reader);
                Require(options.Protocol == OtlpExportProtocol.HttpProtobuf);
                Require(reader.TemporalityPreference == MetricReaderTemporalityPreference.Cumulative);
                Require(reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds == 60_000);
                var pipeline = container.GetRequiredService<CameraPipeline>();
                await VerifyCollectorTransport(collectorFactory!, pipeline);
                pipeline.Configure(new("synthetic-camera", "Synthetic", "http://camera.invalid/"),
                    [new(new("synthetic-rule", "synthetic-camera", "synthetic-topic", Enabled: true), new("http://recorder.invalid/"))]);
                Require(pipeline.ManualTest("synthetic-rule"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try
                {
                    await container.GetRequiredService<HttpActionDispatcher>().StopAsync(cancelled.Token);
                    Require(false);
                }
                catch (OperationCanceledException) { }
                var disposal = container.DisposeAsync().AsTask();
                Require(!disposal.IsCompleted);
                release.TrySetResult();
                await disposal.WaitAsync(TimeSpan.FromSeconds(5));
                Require(finalCapture.Values["bridge.dispatch.outcomes:Cancelled"] == 1);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ONVIF_METRICS_ENABLED", previous);
            }
        }

        private static async Task VerifyCollectorTransport(Func<HttpClient> factory, CameraPipeline pipeline)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            var unexpected = false;
            app.MapPost("/v1/metrics", () => Results.Ok());
            app.MapPost("/redirect", () => Results.Redirect("/unexpected"));
            app.MapGet("/unexpected", () => { unexpected = true; return Results.Ok(); });
            await app.StartAsync();
            var origin = new Uri(app.Urls.Single());
            using var client = factory();
            using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/v1/metrics")))
            using (var response = client.Send(request))
            {
                Require(response.IsSuccessStatusCode);
            }
            using (var response = await client.PostAsync(new Uri(origin, "/redirect"), null))
            {
                Require(response.StatusCode == HttpStatusCode.Found && !unexpected);
            }
            pipeline.SetSelfOrigins([origin]);
            foreach (var address in new[] { new Uri(origin, "/v1/metrics"), new Uri("http://169.254.1.1/v1/metrics") })
            {
                try
                {
                    using var response = await client.PostAsync(address, null);
                    Require(false);
                }
                catch (HttpRequestException) { }
            }
            pipeline.SetSelfOrigins([]);
            await app.StopAsync();
        }

        private static CameraPipeline Pipeline(HttpActionDispatcher dispatcher, string secret, int retries = 0)
        {
            var pipeline = new CameraPipeline(new RuleState(new EventHistory()), dispatcher.TrySubmit, TimeProvider.System);
            pipeline.Configure(new(secret, secret, "http://camera.invalid/" + secret, secret, secret),
                [new(new(secret, secret, secret, Enabled: true), new("http://recorder.invalid/" + secret, additionalAttempts: retries))]);
            return pipeline;
        }

        private static void Require(bool success)
        {
            if (!success)
            {
                throw new InvalidOperationException("Synthetic metrics verification failed.");
            }
        }

        private sealed class Capture : BaseExporter<Metric>
        {
            internal Dictionary<string, long> Values { get; } = [];
            internal long HistogramCount;
            internal bool SafeTags = true;
            internal bool ValidBuckets = true;
            public override ExportResult Export(in Batch<Metric> batch)
            {
                HistogramCount = 0;
                foreach (var metric in batch)
                {
                    foreach (ref readonly var point in metric.GetMetricPoints())
                    {
                        var result = "";
                        foreach (var tag in point.Tags)
                        {
                            SafeTags &= tag.Key is "result" or "decision" or "state" or "http.request.method" or "http.response.status_code" or
                                "network.protocol.version" or "cpu.mode" or "gc.heap.generation" or "http.connection.state";
                            if (tag.Key == "result")
                            {
                                result = tag.Value?.ToString();
                            }
                        }
                        if (metric.MetricType == MetricType.LongSum)
                        {
                            Values[metric.Name + ":" + result] = point.GetSumLong();
                        }
                        if (metric.Name == "bridge.dispatch.duration")
                        {
                            HistogramCount += point.GetHistogramCount();
                            var boundaries = new List<double>();
                            foreach (var bucket in point.GetHistogramBuckets())
                            {
                                boundaries.Add(bucket.ExplicitBound);
                            }
                            ValidBuckets &= boundaries.SequenceEqual(BridgeMetrics.DurationBuckets.Append(double.PositiveInfinity));
                        }
                    }
                }
                return ExportResult.Success;
            }
        }

        private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler, IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(this, false);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        }

        private sealed class Wire : HttpMessageHandler
        {
            internal byte[] Payload = [];
            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Payload = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
            }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
            }
        }
    }
}
