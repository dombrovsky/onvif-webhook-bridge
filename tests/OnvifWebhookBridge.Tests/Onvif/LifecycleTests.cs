namespace OnvifWebhookBridge.Tests.Onvif
{
    using System.Collections.Concurrent;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Onvif;
    using OnvifWebhookBridge.Rules;
    using Xunit;

    [Trait("Category", "Unit")]
    public sealed class LifecycleTests
    {
        [Fact]
        public void LeaseAndBackoffBudgetsAreBounded()
        {
            var now = DateTime.UnixEpoch;
            Assert.Throws<InvalidOperationException>(() => SubscriptionTiming.Lease(now, now.AddSeconds(-1)));
            Assert.Throws<InvalidOperationException>(() => SubscriptionTiming.Lease(now, now.AddSeconds(1)));
            Assert.Equal(120, SubscriptionTiming.Lease(now, now.AddSeconds(120)));
            Assert.True(SubscriptionTiming.Renew(30)); Assert.False(SubscriptionTiming.Renew(31));
            for (var remaining = 31; remaining <= 120; remaining++)
            { Assert.True(SubscriptionTiming.PollSeconds(remaining) + 10 < remaining); }
            Assert.Equal(20, SubscriptionTiming.PollSeconds(double.MaxValue));
            Assert.Equal(TimeSpan.FromSeconds(.75), SubscriptionTiming.Backoff(1, 0));
            Assert.Equal(TimeSpan.FromSeconds(1), SubscriptionTiming.Backoff(1, 1));
            Assert.Equal(TimeSpan.FromSeconds(22.5), SubscriptionTiming.Backoff(int.MaxValue, 0));
            Assert.Equal(TimeSpan.FromSeconds(30), SubscriptionTiming.Backoff(int.MaxValue, 1));
        }

        [Fact]
        public async Task ReconciliationJoinsCleanupBeforeReplacementAndStopsAllWorkers()
        {
            var pipeline = CreatePipeline();
            var a = new CameraConfiguration("a", "Synthetic A", "http://synthetic.invalid/a");
            var b = a with { Id = "b" };
            var started = new ConcurrentQueue<CameraConfiguration>();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = new ConcurrentDictionary<string, int>();
            var maximum = new ConcurrentDictionary<string, int>();
            async Task Run(CameraConfiguration c, Action<CameraStatus> report, CancellationToken token)
            {
                var count = active.AddOrUpdate(c.Id, 1, (_, n) => n + 1);
                maximum.AddOrUpdate(c.Id, count, (_, n) => Math.Max(n, count));
                started.Enqueue(c); report(new(c.Id, "Subscribed"));
                if (started.Count == 2) { both.TrySetResult(); }
                if (c.Name == "Edited") { replaced.TrySetResult(); }
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                if (c.Id == "a" && c.Name != "Edited") { cancelled.TrySetResult(); await cleanup.Task; }
                active.AddOrUpdate(c.Id, 0, (_, n) => n - 1);
            }
            pipeline.ConfigureCameras([a, b], []);
            using var supervisor = new CameraSupervisor(pipeline, Run);
            await supervisor.StartAsync(default);
            await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pipeline.ConfigureCameras([a with { Name = "Edited" }, b], []);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(replaced.Task.IsCompleted);
            Assert.Equal(1, active["b"]);
            // Rapid changes while cleanup is blocked are coalesced to the newest snapshot.
            pipeline.ConfigureCameras([a with { Name = "Discarded" }, b], []);
            pipeline.ConfigureCameras([a with { Name = "Edited" }, b], []);
            cleanup.SetResult();
            await replaced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, started.Count);
            Assert.All(maximum.Values, n => Assert.Equal(1, n));
            await supervisor.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(active.Values, n => Assert.Equal(0, n));
        }

        [Fact]
        public async Task CameraRevisionsAreIndependentAndRecreationCannotReviveStalePullsOrJobs()
        {
            var history = new EventHistory(); var queue = new DispatchQueue();
            var pipeline = new CameraPipeline(new(history), queue, TimeProvider.System);
            var a = new CameraConfiguration("camera", "Synthetic", "http://synthetic.invalid/a");
            var b = a with { Id = "b" };
            var action = new HttpAction("http://destination.invalid");
            ActionRule[] rules = [new(new("a", a.Id, "topic", Enabled: true), action), new(new("b", b.Id, "topic", Enabled: true), action)];
            pipeline.ConfigureCameras([a, b], rules);
            var old = pipeline.Cameras.Single(c => c.Id == a.Id);
            Assert.True(pipeline.ManualTest("a")); Assert.True(pipeline.ManualTest("b"));
            var jobA = await queue.TakeAsync(default); var jobB = await queue.TakeAsync(default);
            pipeline.ConfigureCameras([a with { Password = "synthetic", Username = "synthetic" }, b], rules);
            Assert.False(pipeline.Eligible(jobA)); Assert.True(pipeline.Eligible(jobB));
            pipeline.ConfigureCameras([b], [rules[1]]);
            pipeline.ConfigureCameras([a, b], rules);
            Assert.False(pipeline.IsCurrentCamera(old)); Assert.False(pipeline.Eligible(jobA));
            var notification = Events.Phase2Tests.Event("true") with { Topic = "topic" };
            Assert.All(pipeline.Process(notification, old), d => Assert.Equal(DecisionKind.Disabled, d.Kind));
            Assert.Single(history.Snapshot());
            Assert.Throws<ArgumentException>(() => pipeline.ConfigureCameras(Enumerable.Repeat(a, 65), []));
            Assert.Equal(2, pipeline.Cameras.Count);
        }

        private static CameraPipeline CreatePipeline() => new(new(new EventHistory()), new(), TimeProvider.System);

        [Fact]
        public void SubscriptionEpochResetIsPerCameraAndPreservesCooldown()
        {
            var pipeline = CreatePipeline();
            var a = new CameraConfiguration("camera", "Synthetic A", "http://synthetic.invalid/a");
            var b = a with { Id = "b" };
            var condition = new FieldCondition(FieldSection.Data, "Flag", "true", ComparisonMode.Boolean);
            ActionRule Rule(string id, string camera) => new(new(id, camera, "topic", condition, true,
                OccurrenceMode.Transition, TimeSpan.FromMinutes(1)), new("http://destination.invalid"));
            pipeline.ConfigureCameras([a, b], [Rule("a", a.Id), Rule("b", b.Id)]);
            var current = pipeline.Cameras.Single(c => c.Id == a.Id);
            var no = Events.Phase2Tests.Event("false") with { Topic = "topic" };
            var yes = Events.Phase2Tests.Event("true") with { Topic = "topic" };
            // Use the fixture's generic field name explicitly.
            no = no with { Data = [new("SimpleItem", "Flag", "false", "")] };
            yes = yes with { Data = [new("SimpleItem", "Flag", "true", "")] };
            pipeline.Process(no); pipeline.Process(no with { CameraId = b.Id });
            Assert.Equal(DecisionKind.Accepted, pipeline.Process(yes)[0].Kind);
            Assert.Equal(DecisionKind.Accepted, pipeline.Process(yes with { CameraId = b.Id })[1].Kind);
            pipeline.ResetEpoch(current);
            Assert.Equal(DecisionKind.Baseline, pipeline.Process(yes)[0].Kind);
            Assert.Equal(DecisionKind.Unchanged, pipeline.Process(yes with { CameraId = b.Id })[1].Kind);
            pipeline.Process(no);
            Assert.Equal(DecisionKind.Cooldown, pipeline.Process(yes)[0].Kind);
        }
    }
}
