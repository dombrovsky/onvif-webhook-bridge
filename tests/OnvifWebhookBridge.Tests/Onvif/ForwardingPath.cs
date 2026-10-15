namespace OnvifWebhookBridge.Tests.Onvif
{
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Hosting.Server;
    using Microsoft.AspNetCore.Hosting.Server.Features;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Onvif;
    using OnvifWebhookBridge.Rules;
    using System.Collections.Concurrent;
    using System.Net;
    using System.Net.Http.Json;
    using System.Text;

    // Runs unchanged under xUnit and Linux NativeAOT. The ONVIF source is always onvifsim.
    internal static class ForwardingPath
    {
        public static async Task Verify(Uri endpoint, Uri controlAddress, CancellationToken token)
        {
            const string secret = "synthetic-forwarding-secret";
            const string topic = "Synthetic/Phase3/Detection";
            var captures = new ConcurrentQueue<(string Path, string Method, string Header, string Body)>();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            await using var recorder = builder.Build();
            recorder.Map("/{**path}", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(token);
                captures.Enqueue((context.Request.Path.Value!, context.Request.Method, context.Request.Headers["X-Synthetic"].ToString(), body));
                context.Response.StatusCode = context.Request.Path == "/failure" ? 500 : context.Request.Path == "/redirect" ? 302 : 204;
                if (context.Request.Path == "/redirect")
                {
                    context.Response.Headers.Location = "/redirect-target";
                }
            });
            await recorder.StartAsync(token);
            var origin = recorder.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var logs = new SafeLogCapture();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(logs));
            services.AddCameraPipeline();
            await using var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<CameraPipeline>();
            var history = provider.GetRequiredService<EventHistory>();
            var queue = provider.GetRequiredService<DispatchQueue>();
            ForwardingRule Match(string id) => new(id, "camera", topic,
                new(FieldSection.Data, "State", "true", ComparisonMode.Boolean), true);
            // State is onvifsim's documented fallback only.
            ActionRule[] actions =
            [
                new(Match("get"), new(origin + "/get?synthetic=" + secret, headers: [new("X-Synthetic", secret)])),
                new(Match("post"), new(origin + "/post", "POST", [new("X-Synthetic", secret)], "{\"synthetic\":\"" + secret + "\"}")),
                new(Match("failure"), new(origin + "/failure")),
                new(Match("redirect") with { Enabled = false }, new(origin + "/redirect", headers: [new("X-Synthetic", secret)]))
            ];
            actions = actions.Concat(Enumerable.Range(0, 4).Select(i => new ActionRule(Match("fill" + i) with { Enabled = false }, new(origin + "/fill")))).ToArray();
            pipeline.Configure(new("camera", "Synthetic camera", endpoint.AbsoluteUri, "synthetic-user", "synthetic-password"), actions);
            var hosted = provider.GetServices<IHostedService>().ToArray();
            var worker = hosted.OfType<CameraSupervisor>().Single();
            var dispatcher = hosted.OfType<HttpActionDispatcher>().Single();
            using var control = new HttpClient { BaseAddress = controlAddress };
            try
            {
                await worker.StartAsync(token);
                await Until(() => worker.Status.Single().State == "Subscribed", token);
                // Hold execution to deterministically exercise real-event overflow without blocking polling.
                for (var rule = 0; rule < 4; rule++)
                {
                    for (var i = 0; i < 25; i++)
                    {
                        Check(pipeline.ManualTest("fill" + rule), "FillQueue");
                    }
                }
                await Trigger(true);
                await Until(() => history.Snapshot().Any(e => e.Topic == topic), token);
                Check(queue.Rejected == 3, "EventOverflow");
                await dispatcher.StartAsync(token);
                await Until(() => captures.Count == 100, token);
                await Trigger(false);
                await Until(() => history.Snapshot().Any(e => e.Topic == topic && e.Data.Any(f => f.Value == "false")), token);
                await Trigger(true);
                await Until(() => captures.Count >= 103 && pipeline.Status.All(s => s.Result is ExecutionResult.Accepted or ExecutionResult.HttpFailure), token);
                var post = captures.Single(c => c.Path == "/post");
                Check(post.Method == "POST" && post.Header == secret && post.Body == "{\"synthetic\":\"" + secret + "\"}", "PostLiteralHeaders");
                Check(captures.Any(c => c.Path.StartsWith("/get", StringComparison.Ordinal) && c.Method == "GET" && c.Header == secret && c.Body.Length == 0), "GetHeaders");
                Check(pipeline.Status.Single(s => s.RuleId == "failure") is { Result: ExecutionResult.HttpFailure, Attempts: 1, StatusCode: 500 }, "FailureStatus");
                Check(pipeline.ManualTest("post"), "ManualAdmission");
                await Until(() => captures.Count(c => c.Path == "/post") == 2, token);
                Check(pipeline.ManualTest("redirect"), "RedirectAdmission");
                await Until(() => pipeline.LastExecutions.Any(s => s.RuleId == "redirect"), token);
                Check(pipeline.LastExecutions.Single(s => s.RuleId == "redirect") is { Result: ExecutionResult.HttpFailure, StatusCode: 302, Attempts: 1 }, "RedirectStatus");
                Check(!captures.Any(c => c.Path == "/redirect-target"), "RedirectNotFollowed");
                await Trigger(true);
                await Until(() => captures.Count >= 108, token);
                Check(worker.Status.Single().State == "Subscribed", "FailureIsolation");
                Check(!logs.Text.ToString().Contains(secret, StringComparison.Ordinal) &&
                    !logs.Text.ToString().Contains(origin, StringComparison.Ordinal), "LogRedaction");
                Check(!string.Join(" ", pipeline.Status).Contains(secret, StringComparison.Ordinal), "StatusRedaction");
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await worker.StopAsync(shutdown.Token);
                await dispatcher.StopAsync(shutdown.Token);
                await recorder.StopAsync(shutdown.Token);
            }

            async Task Trigger(bool value)
            {
                using var content = JsonContent.Create(new Trigger(topic, value, 0), SimulatorJson.Default.Trigger);
                await content.LoadIntoBufferAsync(token);
                using var response = await control.PostAsync("api/cameras/synthetic-camera/events", content, token);
                Check(response.IsSuccessStatusCode, "Trigger");
            }
        }

        private static async Task Until(Func<bool> condition, CancellationToken token)
        {
            while (!condition())
            {
                await Task.Delay(20, token);
            }
        }
        private static void Check(bool condition, string category)
        {
            if (!condition)
            {
                throw new InvalidOperationException(category);
            }
        }

        private sealed class SafeLogCapture : ILoggerProvider, ILogger
        {
            public StringBuilder Text { get; } = new();
            public ILogger CreateLogger(string categoryName) => this;
            public void Dispose()
            {
            }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            {
                lock (Text)
                {
                    Text.AppendLine(formatter(state, error));
                }
            }
        }
    }
}
