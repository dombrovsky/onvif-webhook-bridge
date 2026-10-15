namespace OnvifWebhookBridge.Tests.Onvif
{
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Http.Json;
    using System.Text;
    using System.Text.Json;
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
    using SharpOnvifClient;
    using SharpOnvifClient.Events;
    using SharpOnvifCommon.Security;
    using SharpOnvifCommon.Soap;

    // Real onvifsim control API/faults; shared unchanged with the Linux native executable.
    internal static class LifecyclePath
    {
        public static async Task Verify(Uri first, Uri second, Uri controlAddress, CancellationToken token)
        {
            const string username = "synthetic-user", password = "synthetic-password", topic = "Synthetic/Phase4/Detection";
            using var control = new HttpClient { BaseAddress = controlAddress, Timeout = TimeSpan.FromSeconds(10) };
            ScenarioCamera Camera(string id, Uri endpoint, int port) => new(id, "Synthetic lifecycle camera", "generic",
                [new(username, password, "Administrator")], new(IPAddress.Any.ToString(), port, endpoint.Host, endpoint.Port, port + 554));
            var scenario = new Scenario(1, new(IPAddress.Any.ToString(), IPAddress.Any.ToString(), false),
                [Camera("a", first, 8000), Camera("b", second, 8001)]);
            using (var content = JsonContent.Create(scenario, SimulatorJson.Default.Scenario))
            {
                await content.LoadIntoBufferAsync(token);
                using var result = await control.PostAsync("api/scenario", content, token);
                Check(result.IsSuccessStatusCode, "LifecycleScenario");
            }
            var captures = new ConcurrentQueue<string>();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            await using var recorder = builder.Build();
            recorder.Map("/{**path}", (HttpContext c) => { captures.Enqueue(c.Request.Path.Value!); c.Response.StatusCode = 204; });
            await recorder.StartAsync(token);
            var origin = recorder.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var services = new ServiceCollection(); services.AddLogging(b => b.ClearProviders()); services.AddCameraPipeline();
            await using var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<CameraPipeline>();
            var history = provider.GetRequiredService<EventHistory>();
            CameraConfiguration a = new("a", "Synthetic A", first.AbsoluteUri, username, password);
            CameraConfiguration b = new("b", "Synthetic B", second.AbsoluteUri, username, password);
            ActionRule Rule(string id, string camera, OccurrenceMode mode = OccurrenceMode.Occurrence) => new(
                new(id, camera, topic, new(FieldSection.Data, "State", "true", ComparisonMode.Boolean), true, mode), new(origin + "/" + id));
            // State is only the documented onvifsim fallback for this synthetic topic.
            ActionRule[] rules = [Rule("a", "a"), Rule("b", "b"), Rule("transition", "a", OccurrenceMode.Transition)];
            void Configure() => pipeline.ConfigureCameras([a, b], rules);
            Configure();
            var hosted = provider.GetServices<IHostedService>().ToArray();
            var supervisor = hosted.OfType<CameraSupervisor>().Single();
            var dispatcher = hosted.OfType<HttpActionDispatcher>().Single();
            CameraStatus Status(string id) => supervisor.Status.Single(s => s.CameraId == id);
            async Task Ready(string id) => await Until(() => Status(id).State == "Subscribed", token, "Ready" + id);
            async Task HealthyB()
            {
                var count = captures.Count(c => c == "/b");
                await Trigger("b", true);
                await Until(() => captures.Count(c => c == "/b") > count, token, "IndependentBDelivery");
                Check(Status("b").State == "Subscribed" && Status("b").Epoch == 1, "IndependentBEpoch");
            }
            try
            {
                await dispatcher.StartAsync(token); await supervisor.StartAsync(token);
                await Ready("a"); await Ready("b");
                await Trigger("a", false); await Seen("a", false);
                await Trigger("a", true);
                await Until(() => captures.Count(c => c == "/transition") == 1, token, "InitialTransition");
                await HealthyB();
                Check((await Sessions("a")).Length == 1 && (await Sessions("b")).Length == 1, "OneSubscriptionEach");

                // Two idle long polls must update contact without a new epoch or received event.
                await Patch("a", "{\"quirks\":{\"events.pull_always_empty\":true}}");
                var lastEvent = Status("a").LastEvent;
                var contact = Status("a").LastContact;
                await Until(() => Status("a").LastContact > contact, token, "EmptyPollOne");
                contact = Status("a").LastContact;
                await Until(() => Status("a").LastContact > contact, token, "EmptyPollTwo");
                Check(Status("a").Epoch == 1 && Status("a").LastEvent == lastEvent && Status("a").State == "Subscribed", "EmptyPollHealthy");
                await Patch("a", "{\"quirks\":{\"events.pull_always_empty\":false}}");

                // Shorten the real simulator lease over ONVIF, then make a real poll reveal it.
                var address = (await Sessions("a")).Single();
                await Renew(address, "PT25S");
                var before = Status("a").LastContact;
                await Trigger("a", false);
                await Until(async () => (await Termination("a")) > DateTimeOffset.UtcNow.AddSeconds(60), token, "LeaseRenewed");
                Check(Status("a").Epoch == 1 && Status("a").LastContact > before, "RenewWithoutResubscribe");

                // Invalid subscription: removing it at the ONVIF endpoint faults the outstanding pull.
                var epoch = Status("a").Epoch;
                await Invalidate("a");
                await Until(() => Status("a").Epoch > epoch && Status("a").State == "Subscribed", token, "InvalidSubscriptionRecovery");
                await Trigger("a", true); await Seen("a", true);
                Check(captures.Count(c => c == "/transition") == 1, "ReconnectBaseline");
                await Trigger("a", false); await Seen("a", false);
                await Trigger("a", true);
                await Until(() => captures.Count(c => c == "/transition") == 2, token, "TransitionAfterReconnect");
                await HealthyB();

                // Expire an existing real lease while its long poll is outstanding.
                epoch = Status("a").Epoch;
                await Renew((await Sessions("a")).Single(), "PT1S");
                await Until(() => Status("a").Epoch > epoch && Status("a").State == "Subscribed", token, "ExpiredSubscriptionRecovery");
                await HealthyB();

                epoch = Status("a").Epoch;
                await Renew((await Sessions("a")).Single(), "PT25S");
                await Patch("a", "{\"quirks\":{\"events.renew_fails\":true}}");
                await Trigger("a", false);
                await Until(() => Status("a").Epoch > epoch, token, "RenewFailureResubscribe");
                await Patch("a", "{\"quirks\":{\"events.renew_fails\":false}}");
                Check((await Sessions("a")).Length == 1, "RenewFailureCleanup");
                await HealthyB();

                await Patch("a", "{\"quirks\":{\"events.subscription_expires_at_once\":true}}");
                await Invalidate("a");
                await Until(() => Status("a").Failures >= 3 && Status("a").State == "Error", token, "ExpiredLeaseErrorBackoff");
                Check((await Sessions("a")).Length == 0, "ExpiredLeaseCleanup");
                await HealthyB();
                await Patch("a", "{\"quirks\":{\"events.subscription_expires_at_once\":false}}");
                await Ready("a");

                await Patch("a", "{\"quirks\":{\"connect.no_get_services\":true}}");
                await Invalidate("a");
                await Until(() => Status("a").State == "Error", token, "UnsupportedServices");
                await HealthyB();
                await Patch("a", "{\"quirks\":{\"connect.no_get_services\":false}}");
                await Ready("a");

                // Wrong synthetic credentials are a configuration edit; restore them after observing failure.
                a = a with { Password = "synthetic-wrong-secret" }; Configure();
                await Until(() => Status("a").State == "Error", token, "AuthenticationFault");
                await HealthyB();
                a = a with { Password = password }; Configure(); await Ready("a");

                epoch = Status("a").Epoch;
                using (var response = await control.PostAsync("api/cameras/a/offline?seconds=3", null, token))
                { Check(response.IsSuccessStatusCode, "OfflineControl"); }
                await HealthyB();
                await Until(() => Status("a").Epoch > epoch && Status("a").State == "Subscribed", token, "NetworkRestartRecovery");
                Check((await Sessions("a")).Length == 1, "RecoveredRemoteSubscriptionCleanup");
                await Trigger("a", true); await Seen("a", true);

                // Name/connection edits, disable/enable and deletion reconcile against actual sessions.
                var obsoleteAddress = (await Sessions("a")).Single();
                a = a with { Name = "Edited synthetic camera", Endpoint = new Uri(first, "/onvif/events_service").AbsoluteUri }; Configure();
                await Until(async () => Status("a").State == "Subscribed" && (await Sessions("a")) is [var replacement] && replacement != obsoleteAddress, token, "ConnectionEdit");
                await Trigger("a", true);
                await Until(() => history.Snapshot().Any(e => e.CameraName == "Edited synthetic camera"), token, "EditedWorkerObservation");
                a = a with { Enabled = false }; Configure();
                await Until(async () => Status("a").State == "Disabled" && (await Sessions("a")).Length == 0, token, "DisableCleanup");
                await HealthyB();
                a = a with { Enabled = true }; Configure(); await Ready("a");
                await Until(async () => (await Sessions("a")).Length == 1, token, "EnabledSingleSession");

                // Cleanup is bounded even when the simulator delays unsubscribe beyond its deadline.
                await Patch("a", "{\"quirks\":{\"transport.global_delay\":{\"enabled\":true,\"params\":{\"ms\":5000}}}}");
                var began = Stopwatch.GetTimestamp();
                a = a with { Enabled = false }; Configure();
                await Until(() => Status("a").State == "Disabled", token, "BoundedCleanup");
                Check(Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5), "CleanupDeadline");
                await Patch("a", "{\"quirks\":{\"transport.global_delay\":false}}");
                pipeline.ConfigureCameras([b], [rules[1]]);
                await Until(() => supervisor.Status.Count == 1, token, "DeletedStatus");
                await HealthyB();
                Check(history.Snapshot().Any(e => e.CameraId == "a") && history.Snapshot().Any(e => e.CameraId == "b"), "SharedInspector");
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var began = Stopwatch.GetTimestamp();
                await supervisor.StopAsync(shutdown.Token);
                Check(Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5), "GracefulStopDeadline");
                await dispatcher.StopAsync(shutdown.Token);
                await recorder.StopAsync(shutdown.Token);
            }
            Check((await Sessions("a")).Length == 0 && (await Sessions("b")).Length == 0, "FinalSubscriptionCleanup");
            await Until(async () => { using var da = await SessionDocument("a"); using var db = await SessionDocument("b");
                return da.RootElement.GetProperty("httpClients").GetInt32() == 0 && db.RootElement.GetProperty("httpClients").GetInt32() == 0; }, token, "FinalClientCleanup");

            async Task Trigger(string id, bool value)
            {
                var previous = history.Snapshot().LastOrDefault(e => e.CameraId == id && e.Topic == topic);
                using var content = JsonContent.Create(new Trigger(topic, value, 0), SimulatorJson.Default.Trigger);
                await content.LoadIntoBufferAsync(token);
                using var response = await control.PostAsync("api/cameras/" + id + "/events", content, token);
                Check(response.IsSuccessStatusCode, "LifecycleTrigger");
                await Until(() => !ReferenceEquals(previous, history.Snapshot().LastOrDefault(e => e.CameraId == id && e.Topic == topic)), token, "TriggeredObservation");
            }
            async Task Seen(string id, bool value)
            {
                // Look at the newest observation to distinguish repeated synthetic cycles.
                await Until(() => history.Snapshot().LastOrDefault(e => e.CameraId == id && e.Topic == topic)?.Data
                    .Any(f => f.Value == (value ? "true" : "false")) == true, token, "ObservedState");
            }
            async Task Patch(string id, string json)
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await control.PatchAsync("api/cameras/" + id, content, token);
                Check(response.IsSuccessStatusCode, "FaultControl");
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                Check(!body.RootElement.TryGetProperty("warnings", out var warnings) || warnings.GetArrayLength() == 0, "SupportedFaultControl");
            }
            async Task<JsonDocument> SessionDocument(string id) => JsonDocument.Parse(await control.GetStringAsync("api/cameras/" + id + "/sessions", token));
            async Task<string[]> Sessions(string id)
            {
                using var doc = await SessionDocument(id);
                return doc.RootElement.GetProperty("subscriptions").EnumerateArray().Select(s => s.GetProperty("address").GetString()!).ToArray();
            }
            async Task<DateTimeOffset> Termination(string id)
            {
                using var doc = await SessionDocument(id);
                return doc.RootElement.GetProperty("subscriptions")[0].GetProperty("terminationTime").GetDateTimeOffset();
            }
            async Task Renew(string address, string duration)
            {
                var auth = new OnvifAuthenticationSettings(DigestAuthentication.HttpDigest | DigestAuthentication.WsUsernameToken);
                using var manager = new SubscriptionManagerClient(address, new OnvifClientSettings
                { Credentials = new NetworkCredential(username, password), Authentication = new AddressedAuthentication(auth, address), Timeout = TimeSpan.FromSeconds(5) });
                await manager.RenewAsync(new RenewRequest { TerminationTime = duration }, token);
            }
            async Task Invalidate(string id)
            {
                using var client = new SimpleOnvifClient(first.AbsoluteUri, username, password);
                await client.PullPointUnsubscribeAsync((await Sessions(id)).Single()).WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        }

        private static Task Until(Func<bool> condition, CancellationToken token, string category)
            => Until(() => Task.FromResult(condition()), token, category);
        private static async Task Until(Func<Task<bool>> condition, CancellationToken token, string category)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try { while (!await condition()) { await Task.Delay(25, deadline.Token); } }
            catch (OperationCanceledException) { throw new InvalidOperationException(category); }
        }
        private static void Check(bool condition, string category)
        { if (!condition) { throw new InvalidOperationException(category); } }
    }
}
