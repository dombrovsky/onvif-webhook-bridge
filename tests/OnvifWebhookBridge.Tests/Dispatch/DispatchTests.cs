namespace OnvifWebhookBridge.Tests.Dispatch
{
    using System.Net;
    using System.Net.Http.Headers;
    using System.Text;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Rules;
    using OnvifWebhookBridge.Tests.Events;
    using Xunit;

    [Trait("Category", "Unit")]
    public sealed class DispatchTests
    {
        private const string FakeSecret = "synthetic-secret-DO-NOT-LOG";
        private static readonly CameraConfiguration Camera = new("camera", "Synthetic camera", "http://camera.invalid/device");
        private static ActionRule Rule(string id = "rule", HttpAction? action = null) =>
            new(Phase2Tests.Rule(id, OccurrenceMode.Occurrence), action ?? new("http://recorder.invalid/action"));

        [Theory]
        [InlineData("file:///synthetic", "GET", null)]
        [InlineData("http://user:password@synthetic.invalid", "GET", null)]
        [InlineData("http://synthetic.invalid/#fragment", "GET", null)]
        [InlineData("http://synthetic.invalid/\rsecret", "GET", null)]
        [InlineData("http://synthetic.invalid/", "PUT", null)]
        [InlineData("http://synthetic.invalid/", "GET", "{}")]
        [InlineData("http://synthetic.invalid/", "POST", "bad-json-secret")]
        [InlineData("http://metadata.google.internal/", "GET", null)]
        public void RejectsUnsafeActionWithoutEchoingInput(string url, string method, string? body)
        {
            var error = Assert.Throws<ArgumentException>(() => new HttpAction(url, method, jsonBody: body));
            Assert.DoesNotContain("secret", error.Message);
        }

        [Theory]
        [InlineData("Host", "synthetic")]
        [InlineData("Content-Length", "1")]
        [InlineData("Transfer-Encoding", "chunked")]
        [InlineData("Authorization", "token\r\nsecret")]
        [InlineData("Bad Name", "secret")]
        public void RejectsTransportHeadersAndInjection(string name, string value) =>
            Assert.Throws<ArgumentException>(() => new HttpAction("http://synthetic.invalid", headers: [new(name, value)]));

        [Fact]
        public void ConfigurationBoundsAreEnforcedAndHeadersDetached()
        {
            var headers = new[] { new ActionHeader("X-Test", "synthetic") };
            var action = new HttpAction("http://synthetic.invalid", headers: headers);
            headers[0] = new("X-Test", "changed");
            Assert.Equal("synthetic", action.Headers[0].Value);
            Assert.Throws<ArgumentException>(() => new HttpAction("http://synthetic.invalid", additionalAttempts: 3));
            Assert.Throws<ArgumentException>(() => new HttpAction("http://synthetic.invalid", "POST", jsonBody: "\"" + new string('x', 65536) + "\""));
            Assert.Throws<ArgumentException>(() => new HttpAction("http://synthetic.invalid", headers: Enumerable.Range(0, 33).Select(i => new ActionHeader("X-" + i, "v"))));
            Assert.Throws<ArgumentException>(() => new HttpAction("http://synthetic.invalid", headers: [new("X-a", "1"), new("x-A", "2")]));
            Assert.True(HttpAction.ForbiddenAddress(new IPAddress([169, 254, 1, 1])));
            Assert.True(HttpAction.ForbiddenAddress(IPAddress.Parse("fe80::1")));
            using var handler = HttpActionDispatcher.CreateHandler();
            Assert.False(handler.AllowAutoRedirect);
            Assert.False(handler.UseCookies);
            Assert.False(handler.UseProxy);
        }

        [Fact]
        public async Task QueueRotatesRulesSerializesAndRejectsOverflow()
        {
            var queue = new DispatchQueue();
            var action = Rule().Action;
            DispatchJob Job(string id) => new(Guid.NewGuid(), id, 1, 1, action, false, 0, default);
            for (var i = 0; i < 25; i++) { Assert.True(queue.TryEnqueue(Job("busy"))); }
            Assert.False(queue.TryEnqueue(Job("busy")));
            Assert.True(queue.TryEnqueue(Job("other")));
            for (var i = 0; i < 25; i++) { Assert.True(queue.TryEnqueue(Job("third"))); }
            for (var i = 0; i < 25; i++) { Assert.True(queue.TryEnqueue(Job("fourth"))); }
            for (var i = 0; i < 24; i++) { Assert.True(queue.TryEnqueue(Job("fifth"))); }
            Assert.False(queue.TryEnqueue(Job("overflow")));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = await queue.TakeAsync(deadline.Token);
            var other = await queue.TakeAsync(deadline.Token);
            Assert.Equal("busy", first.RuleId); Assert.Equal("other", other.RuleId);
            var third = await queue.TakeAsync(deadline.Token);
            var fourth = await queue.TakeAsync(deadline.Token);
            var fifth = await queue.TakeAsync(deadline.Token);
            Assert.Equal("third", third.RuleId); Assert.Equal("fourth", fourth.RuleId); Assert.Equal("fifth", fifth.RuleId);
            var blocked = queue.TakeAsync(deadline.Token);
            Assert.False(blocked.IsCompleted);
            queue.Complete(first);
            Assert.Equal("busy", (await blocked).RuleId);
            Assert.Equal(2, queue.Rejected);
            Assert.Equal(94, queue.Stop().Count);
            Assert.False(queue.TryEnqueue(Job("after-stop")));
        }

        [Fact]
        public async Task OverflowPreservesHistoryAndDoesNotConsumeCooldown()
        {
            var h = new Harness();
            h.Configure(Rule() with { Match = Rule().Match with { Cooldown = TimeSpan.FromMinutes(1) } });
            for (var i = 0; i < 25; i++) { Assert.True(h.Pipeline.ManualTest("rule")); }
            Assert.Equal(DecisionKind.Rejected, h.Pipeline.Process(Phase2Tests.Event("true"))[0].Kind);
            Assert.Single(h.History.Snapshot());
            Assert.Equal(ExecutionResult.Overflow, h.Pipeline.Status.Single().Result);
            var first = await h.Queue.TakeAsync(default); h.Queue.Complete(first);
            Assert.Equal(DecisionKind.Accepted, h.Pipeline.Process(Phase2Tests.Event("true"))[0].Kind);
            Assert.Equal(DecisionKind.Cooldown, h.Pipeline.Process(Phase2Tests.Event("true"))[0].Kind);
            Assert.True(h.Pipeline.ManualTest("rule") == false); // still full; manual bypasses cooldown, not limits
        }

        [Theory]
        [InlineData(200, 2, 1)]
        [InlineData(400, 2, 1)]
        [InlineData(302, 2, 1)]
        [InlineData(500, 0, 1)]
        [InlineData(500, 2, 3)]
        [InlineData(408, 2, 3)]
        [InlineData(429, 2, 3)]
        public async Task RetryIsOptInAndLimitedToTransientResults(int code, int additional, int expected)
        {
            var h = new Harness();
            var responses = new List<TrackedContent>();
            var factory = new Factory((_, _) =>
            {
                var content = new TrackedContent(); responses.Add(content);
                var response = new HttpResponseMessage((HttpStatusCode)code) { Content = content };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            });
            h.Configure(Rule(action: new("http://synthetic.invalid", additionalAttempts: additional)));
            await h.Deliver(factory);
            var status = h.Pipeline.Status.Single();
            Assert.Equal(expected, status.Attempts); Assert.Equal(code, status.StatusCode);
            Assert.Equal(code == 200 ? ExecutionResult.Accepted : ExecutionResult.HttpFailure, status.Result);
            Assert.All(responses, r => { Assert.True(r.Disposed); Assert.False(r.Read); });
        }

        [Fact]
        public async Task RequestHeadersAndLiteralBodyStayIsolatedAndFailureConsumesCooldown()
        {
            var h = new Harness();
            var a = Rule("a", new("http://synthetic.invalid/a", "POST", [new("Authorization", FakeSecret)], "{\"synthetic\":true}"));
            a = a with { Match = a.Match with { Cooldown = TimeSpan.FromMinutes(1) } };
            var b = Rule("b");
            h.Configure(a, b);
            var calls = 0;
            var factory = new Factory(async (request, _) =>
            {
                if (calls++ == 0)
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal(FakeSecret, request.Headers.GetValues("Authorization").Single());
                    Assert.Equal("{\"synthetic\":true}", await request.Content!.ReadAsStringAsync());
                    throw new HttpRequestException(FakeSecret);
                }
                Assert.False(request.Headers.Contains("Authorization"));
                Assert.Null(request.Content);
                return new(HttpStatusCode.NoContent);
            });
            Assert.All(h.Pipeline.Process(Phase2Tests.Event("true")), d => Assert.Equal(DecisionKind.Accepted, d.Kind));
            await h.DrainOne(factory); await h.DrainOne(factory);
            Assert.Equal(ExecutionResult.TransportFailure, h.Pipeline.Status.Single(s => s.RuleId == "a").Result);
            Assert.Equal(ExecutionResult.Accepted, h.Pipeline.Status.Single(s => s.RuleId == "b").Result);
            Assert.DoesNotContain(FakeSecret, string.Join(" ", h.Pipeline.Status));
            Assert.Equal(DecisionKind.Cooldown, h.Pipeline.Process(Phase2Tests.Event("true"))[0].Kind);
        }

        [Theory]
        [InlineData("rule")]
        [InlineData("action")]
        [InlineData("camera")]
        [InlineData("disable")]
        [InlineData("delete")]
        public async Task ObsoleteQueuedActionsNeverSend(string mutation)
        {
            var h = new Harness(); var rule = Rule(); h.Configure(rule);
            Assert.True(h.Pipeline.ManualTest("rule"));
            switch (mutation)
            {
                case "rule": h.Configure(rule with { Match = rule.Match with { Topic = "changed" } }); break;
                case "action": h.Configure(rule with { Action = new("http://changed.invalid") }); break;
                case "camera": h.Pipeline.Configure(Camera with { Password = "synthetic", Username = "synthetic" }, [rule]); break;
                case "disable": h.Pipeline.Configure(Camera with { Enabled = false }, [rule]); break;
                case "delete": h.Pipeline.Configure(null, []); break;
            }
            var factory = new Factory((_, _) => throw new InvalidOperationException("must not send"));
            await h.DrainOne(factory);
            Assert.Equal(0, factory.Calls);
        }

        [Fact]
        public async Task RetryAfterBeyondDeadlineDoesNotRetryAndCancellationStopsTransport()
        {
            var h = new Harness(); h.Configure(Rule(action: new("http://synthetic.invalid", additionalAttempts: 2)));
            await h.Deliver(new Factory((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
                return Task.FromResult(response);
            }));
            Assert.Equal(ExecutionResult.Expired, h.Pipeline.Status.Single().Result);
            Assert.Equal(1, h.Pipeline.Status.Single().Attempts);
            using var cancel = new CancellationTokenSource();
            Assert.True(h.Pipeline.ManualTest("rule", cancel.Token));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivery = h.DrainOne(new Factory(async (_, token) =>
            { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
            await entered.Task; cancel.Cancel(); await delivery;
            Assert.Equal(ExecutionResult.Cancelled, h.Pipeline.Status.Single().Result);
        }

        [Fact]
        public async Task RemovedFactoryLoggersDoNotLeakSeededSecrets()
        {
            var logs = new CaptureLogger();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(logs));
            services.AddCameraPipeline();
            services.AddHttpClient(HttpActionDispatcher.ClientName).ConfigurePrimaryHttpMessageHandler(() =>
                new Handler((_, _) => throw new HttpRequestException(FakeSecret)));
            using var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<CameraPipeline>();
            pipeline.Configure(Camera, [Rule(action: new("http://synthetic.invalid/" + FakeSecret, "POST", [new("Authorization", FakeSecret)], "\"" + FakeSecret + "\""))]);
            Assert.True(pipeline.ManualTest("rule"));
            var queue = provider.GetRequiredService<DispatchQueue>();
            using var dispatcher = new HttpActionDispatcher(queue, pipeline, provider.GetRequiredService<IHttpClientFactory>(), TimeProvider.System);
            await dispatcher.Deliver(await queue.TakeAsync(default), default);
            Assert.Equal(ExecutionResult.TransportFailure, pipeline.Status.Single().Result);
            Assert.DoesNotContain(FakeSecret, logs.Text.ToString());
            Assert.DoesNotContain("synthetic.invalid", logs.Text.ToString());
        }

        [Fact]
        public async Task DeadlineIncludesQueueTimeAndAttemptTimeoutUsesControllableClock()
        {
            var clock = new TestClock(); var h = new Harness(clock); h.Configure(Rule());
            Assert.True(h.Pipeline.ManualTest("rule"));
            clock.Advance(TimeSpan.FromSeconds(30));
            var noSend = new Factory((_, _) => throw new InvalidOperationException("must not send"));
            await h.DrainOne(noSend);
            Assert.Equal(ExecutionResult.Expired, h.Pipeline.LastExecutions.Single().Result);
            Assert.Equal(0, noSend.Calls);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivery = h.Deliver(new Factory(async (_, token) =>
            { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
            await entered.Task;
            clock.Advance(TimeSpan.FromSeconds(10));
            await delivery;
            Assert.Equal(ExecutionResult.Timeout, h.Pipeline.Status.Single().Result);
            Assert.Equal(TimeSpan.FromSeconds(10), h.Pipeline.Status.Single().Duration);
        }

        [Fact]
        public async Task EditDuringRetryDelayInvalidatesNextAttempt()
        {
            var clock = new TestClock(); var h = new Harness(clock);
            var action = Rule(action: new("http://synthetic.invalid", additionalAttempts: 2)); h.Configure(action);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new Factory((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                entered.SetResult(); return Task.FromResult(response);
            });
            var delivery = h.Deliver(factory);
            await entered.Task;
            // Deliver runs synchronously through response handling and schedules its timer before returning.
            h.Configure(action with { Match = action.Match with { Enabled = false } });
            clock.Advance(TimeSpan.FromSeconds(1)); await delivery;
            Assert.Equal(1, factory.Calls);
            Assert.Equal(ExecutionResult.Stale, h.Pipeline.LastExecutions.Single().Result);
        }

        [Fact]
        public async Task TotalDeadlineCancelsAnAttemptAfterRetryDelay()
        {
            var clock = new TestClock(); var h = new Harness(clock);
            h.Configure(Rule(action: new("http://synthetic.invalid", additionalAttempts: 2)));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var delivery = h.Deliver(new Factory(async (_, token) =>
            {
                if (++calls == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
                    return response;
                }
                entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK);
            }));
            clock.Advance(TimeSpan.FromSeconds(20));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(10)); await delivery;
            Assert.Equal(2, calls);
            Assert.Equal(ExecutionResult.Expired, h.Pipeline.LastExecutions.Single().Result);
            Assert.Equal(TimeSpan.FromSeconds(30), h.Pipeline.LastExecutions.Single().Duration);
        }

        [Fact]
        public void CameraEditKeepsInspectorButRejectsOldPullResultsAndActionEditResetsOnlyItsRule()
        {
            var h = new Harness(); var a = Rule("a"); var b = Rule("b");
            a = a with { Match = a.Match with { Cooldown = TimeSpan.FromMinutes(1) } };
            b = b with { Match = b.Match with { Cooldown = TimeSpan.FromMinutes(1) } };
            h.Configure(a, b);
            Assert.All(h.Pipeline.Process(Phase2Tests.Event("true")), d => Assert.Equal(DecisionKind.Accepted, d.Kind));
            h.Configure(a with { Action = new("http://changed.invalid") }, b);
            var decisions = h.Pipeline.Process(Phase2Tests.Event("true"));
            Assert.Equal(DecisionKind.Accepted, decisions[0].Kind);
            Assert.Equal(DecisionKind.Cooldown, decisions[1].Kind);
            h.Pipeline.Configure(Camera with { Name = "Edited synthetic" }, [a, b]);
            Assert.All(h.Pipeline.Process(Phase2Tests.Event("true"), Camera), d => Assert.Equal(DecisionKind.Disabled, d.Kind));
            Assert.Equal(3, h.History.Snapshot().Count);
        }

        [Fact]
        public async Task IdentifiableSelfTargetNeverSends()
        {
            var h = new Harness(); h.Configure(Rule());
            h.Pipeline.SetSelfOrigins([new("http://recorder.invalid")]);
            var factory = new Factory((_, _) => throw new InvalidOperationException("must not send"));
            await h.Deliver(factory);
            Assert.Equal(0, factory.Calls);
            Assert.Equal(ExecutionResult.Stale, h.Pipeline.LastExecutions.Single().Result);
        }

        [Fact]
        public async Task FourActiveJobsAndOnePerRuleShutdownCancelsActiveAndPending()
        {
            var h = new Harness(); h.Configure(Enumerable.Range(0, 6).Select(i => Rule("r" + i)).ToArray());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = 0; var peak = 0; var calls = 0;
            var factory = new Factory(async (_, token) =>
            {
                var current = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref peak, Math.Max(peak, current));
                if (Interlocked.Increment(ref calls) == 4) { entered.SetResult(); }
                try { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }
                finally { Interlocked.Decrement(ref active); }
            });
            foreach (var i in Enumerable.Range(0, 6)) { Assert.True(h.Pipeline.ManualTest("r" + i)); }
            Assert.True(h.Pipeline.ManualTest("r0"));
            using var dispatcher = new HttpActionDispatcher(h.Queue, h.Pipeline, factory, TimeProvider.System);
            await dispatcher.StartAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, peak); Assert.Equal(4, calls);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dispatcher.StopAsync(stop.Token);
            Assert.Equal(0, active);
            Assert.All(h.Pipeline.Status, s => Assert.Equal(ExecutionResult.Cancelled, s.Result));
        }

        private sealed class Harness
        {
            public EventHistory History { get; } = new();
            public DispatchQueue Queue { get; } = new();
            public CameraPipeline Pipeline { get; }
            public TimeProvider Clock { get; }
            public Harness(TimeProvider? clock = null) { Clock = clock ?? TimeProvider.System; Pipeline = new(new(History, Clock), Queue, Clock); }
            public void Configure(params ActionRule[] rules) => Pipeline.Configure(Camera, rules);
            public async Task Deliver(IHttpClientFactory factory) { Assert.True(Pipeline.ManualTest("rule")); await DrainOne(factory); }
            public async Task DrainOne(IHttpClientFactory factory)
            {
                using var dispatcher = new HttpActionDispatcher(Queue, Pipeline, factory, Clock);
                var job = await Queue.TakeAsync(default);
                try { await dispatcher.Deliver(job, default); } finally { Queue.Complete(job); }
            }
        }
        private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
        {
            public int Calls { get; private set; }
            public HttpClient CreateClient(string name) => new(new Handler((r, t) => { Calls++; return send(r, t); })) { Timeout = Timeout.InfiniteTimeSpan };
        }
        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
        private sealed class TrackedContent : HttpContent
        {
            public bool Disposed { get; private set; }
            public bool Read { get; private set; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Read = true; throw new InvalidOperationException("body must not be read"); }
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }
        private sealed class CaptureLogger : ILoggerProvider, ILogger
        {
            public StringBuilder Text { get; } = new();
            public ILogger CreateLogger(string categoryName) => this;
            public void Dispose() { }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { Text.AppendLine(formatter(state, exception)); }
        }
    }
}
