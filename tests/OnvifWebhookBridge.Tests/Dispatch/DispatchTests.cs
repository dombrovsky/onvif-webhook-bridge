namespace OnvifWebhookBridge.Tests.Dispatch
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Rules;
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Tests.Events;
    using OnvifWebhookBridge.Tests.Persistence;
    using System.Net;
    using System.Net.Http.Headers;
    using System.Text;
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
        public async Task PendingCapacityIncludesDispatchedDelegatesUntilTheyActuallyStart()
        {
            using var h = new Harness();
            h.Configure(Enumerable.Range(0, 5).Select(i => Rule("r" + i)).ToArray());
            var accepted = new List<Guid>();
            for (var rule = 0; rule < 4; rule++)
            {
                for (var i = 0; i < 25; i++)
                {
                    Assert.True(h.Pipeline.ManualTest("r" + rule));
                    accepted.Add(h.Pipeline.Status.Single(s => s.RuleId == "r" + rule).JobId);
                }
                Assert.False(h.Pipeline.ManualTest("r" + rule));
            }
            // Even operations already handed to the paused TaskScheduler retain capacity.
            Assert.False(h.Pipeline.ManualTest("r4"));
            Assert.Equal(5, h.Dispatcher.Rejected);
            await h.RunPending(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), 100);
            Assert.Equal(accepted.Order(), h.Logs.Results.Select(r => r.Job).Order());
            Assert.All(h.Logs.Results, r => Assert.Equal(ExecutionResult.Accepted, r.Result));
            Assert.True(h.Pipeline.ManualTest("r4"));
        }

        [Fact]
        public async Task ReadyRulesGetATurnBeforeHotRuleBacklogAndEachRuleIsFifo()
        {
            using var h = new Harness();
            h.Configure(new[] { "hot", "b", "c", "d", "e" }.Select(id =>
                Rule(id, new("http://recorder.invalid/" + id))).ToArray());
            var starts = System.Threading.Channels.Channel.CreateUnbounded<string>();
            using var releaseHot = new SemaphoreSlim(0);
            var releaseOthers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hotIds = new List<Guid>();
            Assert.True(h.Pipeline.ManualTest("hot"));
            hotIds.Add(h.Pipeline.Status.Single().JobId);
            h.Start(new Factory(async (request, token) =>
            {
                var id = request.RequestUri!.AbsolutePath[1..];
                starts.Writer.TryWrite(id);
                if (id == "hot")
                {
                    await releaseHot.WaitAsync(token);
                }
                else
                {
                    await releaseOthers.Task.WaitAsync(token);
                }
                return new(HttpStatusCode.OK);
            }));
            Assert.Equal("hot", await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 25; i++)
            {
                Assert.True(h.Pipeline.ManualTest("hot"));
                hotIds.Add(h.Pipeline.Status.Single().JobId);
            }
            Assert.False(h.Pipeline.ManualTest("hot"));
            foreach (var id in new[] { "b", "c", "d", "e" })
            {
                Assert.True(h.Pipeline.ManualTest(id));
            }
            var firstWave = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                firstWave.Add(await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.Equal(new[] { "b", "c", "d" }, firstWave.Order());
            Assert.False(starts.Reader.TryRead(out _)); // four active, including exactly one hot
            releaseHot.Release();
            Assert.Equal("e", await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(ExecutionResult.Overflow, h.Pipeline.Status.Single(s => s.RuleId == "hot").Result);
            releaseOthers.SetResult();
            releaseHot.Release(25);
            await h.Logs.WaitForCount(30);
            Assert.Equal(hotIds, h.Logs.Results.Where(r => hotIds.Contains(r.Job)).Select(r => r.Job));
            Assert.Equal(30, h.Logs.Results.Count);
            Assert.All(h.Logs.Results, r => Assert.Equal(ExecutionResult.Accepted, r.Result));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task QueuedCancellationAndFastCompletionCannotBeOverwrittenByQueued(bool cancelBeforeSubmission)
        {
            using var h = new Harness();
            h.Configure(Rule());
            using var cancellation = new CancellationTokenSource();
            if (cancelBeforeSubmission)
            {
                cancellation.Cancel();
            }
            Assert.True(h.Pipeline.ManualTest("rule", cancellation.Token));
            if (!cancelBeforeSubmission)
            {
                cancellation.Cancel();
            }
            var factory = new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            await h.RunPending(factory);
            Assert.Equal(0, factory.Calls);
            Assert.Equal(ExecutionResult.Cancelled, h.Pipeline.Status.Single().Result);
            Assert.Single(h.Logs.Results);
            for (var i = 0; i < 100; i++)
            {
                h.Start(factory);
                Assert.True(h.Pipeline.ManualTest("rule"));
                await h.Logs.WaitForCount(i + 2);
                Assert.Equal(ExecutionResult.Accepted, h.Pipeline.Status.Single().Result);
            }
            Assert.Equal(101, h.Logs.Results.Select(r => r.Job).Distinct().Count());
            Assert.Equal(101, h.Logs.Results.Count);
        }

        [Fact]
        public async Task RunningAndOlderTerminalCannotOverwriteNewAdmission()
        {
            using var h = new Harness();
            h.Configure(Rule());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(h.Pipeline.ManualTest("rule"));
            var first = h.Pipeline.Status.Single().JobId;
            h.Start(new Factory(async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new(HttpStatusCode.OK);
            }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ExecutionResult.Running, h.Pipeline.Status.Single().Result);
            using var cancel = new CancellationTokenSource();
            Assert.True(h.Pipeline.ManualTest("rule", cancel.Token));
            var latest = h.Pipeline.Status.Single().JobId;
            cancel.Cancel();
            await h.Logs.WaitForCount(1); // queued cancellation completes while the first request is held
            Assert.Equal(ExecutionResult.Cancelled, h.Pipeline.Status.Single().Result);
            release.SetResult();
            await h.Logs.WaitForCount(2);
            Assert.Equal(latest, h.Pipeline.Status.Single().JobId);
            Assert.Equal(ExecutionResult.Cancelled, h.Pipeline.Status.Single().Result);
            Assert.Equal(2, h.Logs.Results.Count);
            Assert.Contains((first, ExecutionResult.Accepted), h.Logs.Results);
            Assert.Contains((latest, ExecutionResult.Cancelled), h.Logs.Results);
        }

        [Fact]
        public async Task OverflowPreservesHistoryAndDoesNotConsumeCooldown()
        {
            using var h = new Harness();
            h.Configure(Rule() with
            {
                Match = Rule().Match with
                {
                    Cooldown = TimeSpan.FromMinutes(1)
                }
            });
            for (var i = 0; i < 25; i++)
            {
                Assert.True(h.Pipeline.ManualTest("rule"));
            }
            Assert.Equal(DecisionKind.Rejected, h.Pipeline.Process(Phase2Tests.Event("true"))[0].Kind);
            Assert.Single(h.History.Snapshot());
            Assert.Equal(ExecutionResult.Overflow, h.Pipeline.Status.Single().Result);
            await h.RunPending(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), 25);
            Assert.Equal(DecisionKind.Accepted, h.Pipeline.Process(Phase2Tests.Event("true"))[0].Kind);
            Assert.Equal(DecisionKind.Cooldown, h.Pipeline.Process(Phase2Tests.Event("true"))[0].Kind);
            Assert.True(h.Pipeline.ManualTest("rule")); // manual bypasses cooldown
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
            using var h = new Harness();
            var responses = new List<TrackedContent>();
            var factory = new Factory((_, _) =>
            {
                var content = new TrackedContent();
                responses.Add(content);
                var response = new HttpResponseMessage((HttpStatusCode)code) { Content = content };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            });
            h.Configure(Rule(action: new("http://synthetic.invalid", additionalAttempts: additional)));
            await h.SubmitAndRun(factory);
            var status = h.Pipeline.Status.Single();
            Assert.Equal(expected, status.Attempts);
            Assert.Equal(code, status.StatusCode);
            Assert.Equal(code == 200 ? ExecutionResult.Accepted : ExecutionResult.HttpFailure, status.Result);
            Assert.All(responses, r => { Assert.True(r.Disposed); Assert.False(r.Read); });
        }

        [Fact]
        public async Task RequestHeadersAndLiteralBodyStayIsolatedAndFailureConsumesCooldown()
        {
            using var h = new Harness();
            var a = Rule("a", new("http://synthetic.invalid/a", "POST", [new("Authorization", FakeSecret)], "{\"synthetic\":true}"));
            a = a with
            {
                Match = a.Match with
                {
                    Cooldown = TimeSpan.FromMinutes(1)
                }
            };
            var b = Rule("b");
            h.Configure(a, b);
            var factory = new Factory(async (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/a")
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
            await h.RunPending(factory, 2);
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
            using var h = new Harness();
            var rule = Rule();
            h.Configure(rule);
            Assert.True(h.Pipeline.ManualTest("rule"));
            switch (mutation)
            {
                case "rule":
                    h.Configure(rule with
                    {
                        Match = rule.Match with
                        {
                            Topic = "changed"
                        }
                    });
                    break;
                case "action":
                    h.Configure(rule with
                    {
                        Action = new("http://changed.invalid")
                    });
                    break;
                case "camera":
                    h.Pipeline.Configure(Camera with
                    {
                        Password = "synthetic",
                        Username = "synthetic"
                    }, [rule]);
                    break;
                case "disable":
                    h.Pipeline.Configure(Camera with
                    {
                        Enabled = false
                    }, [rule]);
                    break;
                case "delete":
                    h.Pipeline.Configure(null, []);
                    break;
            }
            var factory = new Factory((_, _) => throw new InvalidOperationException("must not send"));
            await h.RunPending(factory);
            Assert.Equal(0, factory.Calls);
        }

        [Fact]
        public async Task RetryAfterBeyondDeadlineDoesNotRetryAndCancellationStopsTransport()
        {
            using var h = new Harness();
            h.Configure(Rule(action: new("http://synthetic.invalid", additionalAttempts: 2)));
            await h.SubmitAndRun(new Factory((_, _) =>
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
            var delivery = h.RunPending(new Factory(async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new(HttpStatusCode.OK);
            }));
            await entered.Task;
            cancel.Cancel();
            await delivery;
            Assert.Equal(ExecutionResult.Cancelled, h.Pipeline.Status.Single().Result);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RemovedFactoryLoggersDoNotLeakSeededSecrets(bool ignoreCertificateValidation)
        {
            var logs = new CaptureLogger();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(logs));
            services.AddCameraPipeline();
            services.AddHttpClient(ignoreCertificateValidation ? HttpActionDispatcher.UnverifiedClientName : HttpActionDispatcher.ClientName).ConfigurePrimaryHttpMessageHandler(() =>
                new Handler((_, _) => throw new HttpRequestException(FakeSecret)));
            await using var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<CameraPipeline>();
            pipeline.Configure(Camera, [Rule(action: new("http://synthetic.invalid/" + FakeSecret, "POST", [new("Authorization", FakeSecret)], "\"" + FakeSecret + "\"", ignoreCertificateValidation: ignoreCertificateValidation))]);
            Assert.True(pipeline.ManualTest("rule"));
            await logs.WaitForCount(1);
            Assert.Equal(ExecutionResult.TransportFailure, pipeline.Status.Single().Result);
            Assert.DoesNotContain(FakeSecret, logs.Text.ToString());
            Assert.DoesNotContain("synthetic.invalid", logs.Text.ToString());
        }

        [Fact]
        public async Task DeadlineIncludesQueueTimeAndAttemptTimeoutUsesControllableClock()
        {
            var clock = new TestClock();
            using var h = new Harness(clock);
            h.Configure(Rule());
            Assert.True(h.Pipeline.ManualTest("rule"));
            clock.Advance(TimeSpan.FromSeconds(30));
            var noSend = new Factory((_, _) => throw new InvalidOperationException("must not send"));
            await h.RunPending(noSend);
            Assert.Equal(ExecutionResult.Expired, h.Pipeline.LastExecutions.Single().Result);
            Assert.Equal(0, noSend.Calls);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivery = h.SubmitAndRun(new Factory(async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new(HttpStatusCode.OK);
            }));
            await entered.Task;
            clock.Advance(TimeSpan.FromSeconds(10));
            await delivery;
            Assert.Equal(ExecutionResult.Timeout, h.Pipeline.Status.Single().Result);
            Assert.Equal(TimeSpan.FromSeconds(10), h.Pipeline.Status.Single().Duration);
        }

        [Fact]
        public async Task ManualCancellationInterruptsRetryDelayWithoutAnotherAttempt()
        {
            var clock = new TestClock();
            using var h = new Harness(clock);
            h.Configure(Rule(action: new("http://recorder.invalid", additionalAttempts: 2)));
            using var cancellation = new CancellationTokenSource();
            Assert.True(h.Pipeline.ManualTest("rule", cancellation.Token));
            var factory = new Factory((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
                return Task.FromResult(response);
            });
            var delivery = h.RunPending(factory);
            await clock.WaitForTimer(TimeSpan.FromSeconds(20));
            cancellation.Cancel();
            await delivery;
            Assert.Equal(1, factory.Calls);
            Assert.Equal(ExecutionResult.Cancelled, h.Pipeline.Status.Single().Result);
            Assert.Equal(1, h.Pipeline.Status.Single().Attempts);
            Assert.Single(h.Logs.Results);
        }

        [Fact]
        public async Task EditDuringRetryDelayInvalidatesNextAttempt()
        {
            var clock = new TestClock();
            using var h = new Harness(clock);
            var action = Rule(action: new("http://synthetic.invalid", additionalAttempts: 2));
            h.Configure(action);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new Factory((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                entered.SetResult();
                return Task.FromResult(response);
            });
            var delivery = h.SubmitAndRun(factory);
            await entered.Task;
            await clock.WaitForTimer(TimeSpan.FromSeconds(1));
            h.Configure(action with
            {
                Match = action.Match with
                {
                    Enabled = false
                }
            });
            clock.Advance(TimeSpan.FromSeconds(1));
            await delivery;
            Assert.Equal(1, factory.Calls);
            Assert.Equal(ExecutionResult.Stale, h.Pipeline.LastExecutions.Single().Result);
        }

        [Fact]
        public async Task TotalDeadlineCancelsAnAttemptAfterRetryDelay()
        {
            var clock = new TestClock();
            using var h = new Harness(clock);
            h.Configure(Rule(action: new("http://synthetic.invalid", additionalAttempts: 2)));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var delivery = h.SubmitAndRun(new Factory(async (_, token) =>
            {
                if (++calls == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
                    return response;
                }
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new(HttpStatusCode.OK);
            }));
            await clock.WaitForTimer(TimeSpan.FromSeconds(20));
            clock.Advance(TimeSpan.FromSeconds(20));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(10));
            await delivery;
            Assert.Equal(2, calls);
            Assert.Equal(ExecutionResult.Expired, h.Pipeline.LastExecutions.Single().Result);
            Assert.Equal(TimeSpan.FromSeconds(30), h.Pipeline.LastExecutions.Single().Duration);
        }

        [Fact]
        public void CameraEditKeepsInspectorButRejectsOldPullResultsAndActionEditResetsOnlyItsRule()
        {
            using var h = new Harness();
            var a = Rule("a");
            var b = Rule("b");
            a = a with
            {
                Match = a.Match with
                {
                    Cooldown = TimeSpan.FromMinutes(1)
                }
            };
            b = b with
            {
                Match = b.Match with
                {
                    Cooldown = TimeSpan.FromMinutes(1)
                }
            };
            h.Configure(a, b);
            Assert.All(h.Pipeline.Process(Phase2Tests.Event("true")), d => Assert.Equal(DecisionKind.Accepted, d.Kind));
            h.Configure(a with
            {
                Action = new("http://changed.invalid")
            }, b);
            var decisions = h.Pipeline.Process(Phase2Tests.Event("true"));
            Assert.Equal(DecisionKind.Accepted, decisions[0].Kind);
            Assert.Equal(DecisionKind.Cooldown, decisions[1].Kind);
            h.Pipeline.Configure(Camera with
            {
                Name = "Edited synthetic"
            }, [a, b]);
            Assert.All(h.Pipeline.Process(Phase2Tests.Event("true"), Camera), d => Assert.Equal(DecisionKind.Disabled, d.Kind));
            Assert.Equal(3, h.History.Snapshot().Count);
        }

        [Fact]
        public async Task IdentifiableSelfTargetNeverSends()
        {
            using var h = new Harness();
            h.Configure(Rule());
            h.Pipeline.SetSelfOrigins([new("http://recorder.invalid")]);
            var factory = new Factory((_, _) => throw new InvalidOperationException("must not send"));
            await h.SubmitAndRun(factory);
            Assert.Equal(0, factory.Calls);
            Assert.Equal(ExecutionResult.Stale, h.Pipeline.LastExecutions.Single().Result);
        }

        [Fact]
        public async Task SchedulerFaultBeforeDelegateStartIsObservedOnceWithoutLeakingDetails()
        {
            var logs = new CaptureLogger();
            var factory = new Factory((_, _) => throw new InvalidOperationException("Must not send."));
            await using var dispatcher = new HttpActionDispatcher(factory, TimeProvider.System,
                new System.Threading.Tasks.Flow.TaskFlow(new()
                {
                    TaskScheduler = new RejectingScheduler()
                }), logs);
            var pipeline = new CameraPipeline(new(new EventHistory()), dispatcher, TimeProvider.System);
            pipeline.Configure(Camera, [Rule()]);
            Assert.True(pipeline.ManualTest("rule"));
            await logs.WaitForCount(1);
            Assert.Equal(ExecutionResult.TransportFailure, pipeline.Status.Single().Result);
            Assert.Equal(0, pipeline.Status.Single().Attempts);
            Assert.Equal(0, factory.Calls);
            Assert.Single(logs.Results);
            Assert.DoesNotContain(FakeSecret, logs.Text.ToString());
        }

        private sealed class RejectingScheduler : TaskScheduler
        {
            protected override void QueueTask(Task task) => throw new InvalidOperationException(FakeSecret);
            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
            protected override IEnumerable<Task>? GetScheduledTasks() => null;
        }

        [Theory]
        [InlineData("equivalent", ExecutionResult.Accepted)]
        [InlineData("tls", ExecutionResult.Stale)]
        [InlineData("recreate", ExecutionResult.Stale)]
        public async Task EquivalentConfigurationRetainsJobsAndTlsEditsOrRecreationInvalidateThem(string edit, ExecutionResult expected)
        {
            using var h = new Harness();
            var rule = Rule();
            using var data = new TemporaryStorage();
            var store = new ConfigurationStore(data.Path, h.Pipeline);
            store.Initialize();
            CameraEdit[] cameras = [new(Camera.Id, Camera.Name, Camera.Endpoint)];
            RuleEdit Edit(bool ignore) => new(rule.Match, "Synthetic action",
                url: StoragePath.Replace(rule.Action.Address.AbsoluteUri), ignoreCertificateValidation: ignore);
            var revision = store.Save(0, cameras, [Edit(false)]);
            Assert.True(h.Pipeline.ManualTest("rule"));
            if (edit == "recreate")
            {
                revision = store.Save(revision, [], []);
            }
            store.Save(revision, cameras, [Edit(edit == "tls")]);
            var factory = new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            await h.RunPending(factory);
            Assert.Equal(expected, h.Pipeline.LastExecutions.Single().Result);
            Assert.Equal(expected == ExecutionResult.Accepted ? 1 : 0, factory.Calls);
            Assert.Single(h.Logs.Results);
        }

        [Fact]
        public async Task CameraEditInvalidatesOnlyItsOwnQueuedJobs()
        {
            using var h = new Harness();
            var other = Camera with
            {
                Id = "other"
            };
            var a = Rule("a", new("http://recorder.invalid/a"));
            var b = Rule("b", new("http://recorder.invalid/b")) with
            {
                Match = Rule("b").Match with
                {
                    CameraId = other.Id
                }
            };
            h.Pipeline.ConfigureCameras([Camera, other], [a, b]);
            Assert.True(h.Pipeline.ManualTest("a"));
            Assert.True(h.Pipeline.ManualTest("b"));
            h.Pipeline.ConfigureCameras([Camera with { Name = "Edited synthetic camera" }, other], [a, b]);
            var sent = new List<string>();
            await h.RunPending(new Factory((request, _) =>
            {
                sent.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }), 2);
            Assert.Equal(new[] { "/b" }, sent);
            Assert.Equal(ExecutionResult.Stale, h.Pipeline.Status.Single(s => s.RuleId == "a").Result);
            Assert.Equal(ExecutionResult.Accepted, h.Pipeline.Status.Single(s => s.RuleId == "b").Result);
        }

        [Fact]
        public async Task FourActiveJobsAndOnePerRuleShutdownCancelsActiveAndPending()
        {
            using var h = new Harness();
            h.Configure(Enumerable.Range(0, 6).Select(i => Rule("r" + i)).ToArray());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = 0;
            var peak = 0;
            var calls = 0;
            var factory = new Factory(async (_, token) =>
            {
                var current = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref peak, Math.Max(peak, current));
                if (Interlocked.Increment(ref calls) == 4)
                {
                    entered.SetResult();
                }
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return new(HttpStatusCode.OK);
                }
                finally { Interlocked.Decrement(ref active); }
            });
            foreach (var i in Enumerable.Range(0, 6))
            {
                Assert.True(h.Pipeline.ManualTest("r" + i));
            }
            Assert.True(h.Pipeline.ManualTest("r0"));
            var dispatcher = h.Dispatcher;
            h.Start(factory);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, peak);
            Assert.Equal(4, calls);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dispatcher.StopAsync(stop.Token);
            Assert.Equal(0, active);
            Assert.All(h.Pipeline.Status, s => Assert.Equal(ExecutionResult.Cancelled, s.Result));
            Assert.Equal(7, h.Logs.Results.Count);
            Assert.Equal(7, h.Logs.Results.Select(r => r.Job).Distinct().Count());
            Assert.All(h.Logs.Results, r => Assert.Equal(ExecutionResult.Cancelled, r.Result));
            Assert.False(h.Pipeline.ManualTest("r0"));
            Assert.Equal(1, h.Dispatcher.Rejected);
        }

        [Theory]
        [InlineData("{\"message\":\"synthetic-secret-DO-NOT-LOG\"}", "application/json", "Json")]
        [InlineData("{\"error\":true}", "application/problem+json", "Json")]
        [InlineData("not json", "application/json", "InvalidJson")]
        [InlineData("", "application/json", "Empty")]
        [InlineData("<html>synthetic</html>", "text/html", "NotJson")]
        public async Task ResponseDiagnosticsRetainOnlyValidJsonWithoutChangingHttpOutcome(string body, string contentType, string expected)
        {
            using var h = new Harness();
            h.Configure(Rule());
            await h.SubmitAndRun(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            })));
            var status = h.Pipeline.LastExecutions.Single();
            Assert.Equal(400, status.StatusCode);
            Assert.Equal(ExecutionResult.HttpFailure, status.Result);
            Assert.Equal(1, status.Attempts);
            Assert.Equal(expected, status.ResponseBodyStatus);
            Assert.Equal(expected == "Json" ? body : null, status.ResponseJson);
        }

        [Fact]
        public async Task OversizedAndDeepJsonAreNotRetained()
        {
            foreach (var body in new[] { "\"" + new string('x', HttpActionDispatcher.MaximumResponseBytes) + "\"", new string('[', 33) + "0" + new string(']', 33) })
            {
                using var h = new Harness();
                h.Configure(Rule());
                await h.SubmitAndRun(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                })));
                var status = h.Pipeline.LastExecutions.Single();
                Assert.Null(status.ResponseJson);
                Assert.Contains(status.ResponseBodyStatus, new[] { "TooLarge", "InvalidJson" });
                Assert.Equal(ExecutionResult.Accepted, status.Result);
            }
        }

        [Fact]
        public async Task LastAttemptClearsEarlierJsonWhenTransportFails()
        {
            using var h = new Harness();
            h.Configure(Rule(action: new("http://synthetic.invalid", additionalAttempts: 1)));
            var calls = 0;
            await h.SubmitAndRun(new Factory((_, _) =>
            {
                if (++calls > 1)
                {
                    throw new HttpRequestException(FakeSecret);
                }
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"prior\":true}", Encoding.UTF8, "application/json")
                };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            }));
            var status = h.Pipeline.LastExecutions.Single();
            Assert.Null(status.StatusCode);
            Assert.Null(status.ResponseJson);
            Assert.Equal(ExecutionResult.TransportFailure, status.Result);
            Assert.DoesNotContain(FakeSecret, status.TransportError ?? "");
        }

        [Fact]
        public async Task UnreadableJsonDoesNotRetryAcceptedAction()
        {
            using var h = new Harness();
            h.Configure(Rule(action: new("http://synthetic.invalid", additionalAttempts: 2)));
            var content = new TrackedContent();
            content.Headers.ContentType = new("application/json");
            var factory = new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
            await h.SubmitAndRun(factory);
            var status = h.Pipeline.LastExecutions.Single();
            Assert.Equal(ExecutionResult.Accepted, status.Result);
            Assert.Equal(200, status.StatusCode);
            Assert.Equal("Unavailable", status.ResponseBodyStatus);
            Assert.Null(status.ResponseJson);
            Assert.Equal(1, factory.Calls);
            Assert.True(content.Disposed);
        }

        [Fact]
        public async Task UnknownLengthResponseStopsAtBound()
        {
            using var h = new Harness();
            h.Configure(Rule());
            using var stream = new UnknownLengthStream(new byte[HttpActionDispatcher.MaximumResponseBytes * 4]);
            var content = new StreamContent(stream);
            content.Headers.ContentType = new("application/json");
            await h.SubmitAndRun(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
            Assert.Equal("TooLarge", h.Pipeline.LastExecutions.Single().ResponseBodyStatus);
            Assert.Equal(HttpActionDispatcher.MaximumResponseBytes + 1, stream.BytesRead);
        }

        [Fact]
        public async Task SlowJsonResponseHonorsAttemptTimeoutWithoutRetryingAcceptedAction()
        {
            var clock = new TestClock();
            using var h = new Harness(clock);
            h.Configure(Rule(action: new("http://synthetic.invalid", additionalAttempts: 2)));
            using var stream = new BlockingReadStream();
            var content = new StreamContent(stream);
            content.Headers.ContentType = new("application/json");
            var factory = new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
            var delivery = h.SubmitAndRun(factory);
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(HttpActionDispatcher.AttemptTimeout);
            await delivery.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ExecutionResult.Accepted, h.Pipeline.LastExecutions.Single().Result);
            Assert.Equal("Unavailable", h.Pipeline.LastExecutions.Single().ResponseBodyStatus);
            Assert.Equal(1, factory.Calls);
        }

        private sealed class BlockingReadStream : MemoryStream
        {
            public override bool CanSeek => false;
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Entered.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }

        [Fact]
        public async Task JsonResponseNeverAppearsInLogs()
        {
            var logs = new CaptureLogger();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(logs));
            services.AddCameraPipeline();
            services.AddHttpClient(HttpActionDispatcher.ClientName).ConfigurePrimaryHttpMessageHandler(() =>
                new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("\"" + FakeSecret + "\"", Encoding.UTF8, "application/json")
                })));
            await using var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<CameraPipeline>();
            pipeline.Configure(Camera, [Rule()]);
            Assert.True(pipeline.ManualTest("rule"));
            await logs.WaitForCount(1);
            Assert.Contains(FakeSecret, pipeline.LastExecutions.Single().ResponseJson!);
            Assert.DoesNotContain(FakeSecret, logs.Text.ToString());
        }

        private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
        {
            public override bool CanSeek => false;
            public int BytesRead
            {
                get; private set;
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var count = await base.ReadAsync(buffer, cancellationToken);
                BytesRead += count;
                return count;
            }
        }

        private sealed class Harness : IDisposable
        {
            public EventHistory History { get; } = new();
            public PausedTaskScheduler Scheduler { get; } = new();
            public HttpActionDispatcher Dispatcher
            {
                get;
            }
            public CaptureLogger Logs { get; } = new();
            public CameraPipeline Pipeline
            {
                get;
            }
            public TimeProvider Clock
            {
                get;
            }
            private readonly SwitchingFactory clients = new();
            private int awaited;
            public Harness(TimeProvider? clock = null)
            {
                Clock = clock ?? TimeProvider.System;
                Dispatcher = new(clients, Clock, new System.Threading.Tasks.Flow.TaskFlow(
                    new()
                    {
                        TaskScheduler = Scheduler
                    }), Logs);
                Pipeline = new(new(History, Clock), Dispatcher, Clock);
            }
            public void Configure(params ActionRule[] rules) => Pipeline.Configure(Camera, rules);
            public void Start(IHttpClientFactory factory)
            {
                clients.Inner = factory;
                Scheduler.Resume();
            }
            public async Task SubmitAndRun(IHttpClientFactory factory)
            {
                Assert.True(Pipeline.ManualTest("rule"));
                await RunPending(factory);
            }
            public async Task RunPending(IHttpClientFactory factory, int count = 1)
            {
                Start(factory);
                await Logs.WaitForCount(awaited += count);
                Scheduler.Pause();
            }
            public void Dispose()
            {
                Scheduler.Resume();
                Dispatcher.Dispose();
            }
            internal sealed class SwitchingFactory : IHttpClientFactory
            {
                public IHttpClientFactory? Inner
                {
                    get; set;
                }
                public HttpClient CreateClient(string name) => Inner!.CreateClient(name);
            }
        }
        private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
        {
            public int Calls
            {
                get; private set;
            }
            public HttpClient CreateClient(string name) => new(new Handler((r, t) => { Calls++; return send(r, t); })) { Timeout = Timeout.InfiniteTimeSpan };
        }
        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
        }
        private sealed class TrackedContent : HttpContent
        {
            public bool Disposed
            {
                get; private set;
            }
            public bool Read
            {
                get; private set;
            }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                Read = true;
                throw new InvalidOperationException("body must not be read");
            }
            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
        private sealed class CaptureLogger : ILoggerProvider, ILogger, ILogger<HttpActionDispatcher>
        {
            public StringBuilder Text { get; } = new();
            public System.Collections.Concurrent.ConcurrentQueue<(Guid Job, ExecutionResult Result)> Results { get; } = new();
            private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task WaitForCount(int count)
            {
                while (true)
                {
                    Task next;
                    lock (Text)
                    {
                        if (Results.Count >= count)
                        {
                            return;
                        }
                        next = changed.Task;
                    }
                    await next.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            public ILogger CreateLogger(string categoryName) => this;
            public void Dispose()
            {
            }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (Text)
                {
                    Text.AppendLine(formatter(state, exception));
                    if (state is IEnumerable<KeyValuePair<string, object?>> values)
                    {
                        var fields = values.ToDictionary(p => p.Key, p => p.Value);
                        if (fields.GetValueOrDefault("JobId") is Guid job && fields.GetValueOrDefault("Result") is ExecutionResult result)
                        {
                            Results.Enqueue((job, result));
                            changed.TrySetResult();
                            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                    }
                }
            }
        }
    }
}
