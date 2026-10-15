namespace OnvifWebhookBridge.Tests.Onvif
{
    using System.Collections.Concurrent;
    using System.Net;
    using System.Net.Http.Json;
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
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Rules;
    using OnvifWebhookBridge.Tests.Persistence;
    using static OnvifWebhookBridge.Tests.Persistence.StoragePath;

    // Actual onvifsim + local recorder, same path under JIT and NativeAOT.
    internal static class PersistencePath
    {
        public static async Task Verify(Uri endpoint, Uri controlAddress, CancellationToken token)
        {
            const string topic = "Synthetic/Phase5/Detection";
            const string secret = "synthetic-persisted-secret";
            using var control = new HttpClient { BaseAddress = controlAddress, Timeout = TimeSpan.FromSeconds(10) };
            var scenario = new Scenario(1, new(IPAddress.Any.ToString(), IPAddress.Any.ToString(), false),
                [new("synthetic-camera", "Synthetic persistence camera", "generic",
                    [new("synthetic-user", "synthetic-password", "Administrator")],
                    new(IPAddress.Any.ToString(), 8000, endpoint.Host, endpoint.Port))]);
            using (var content = JsonContent.Create(scenario, SimulatorJson.Default.Scenario))
            {
                await content.LoadIntoBufferAsync(token);
                using var response = await control.PostAsync("api/scenario", content, token);
                Check(response.IsSuccessStatusCode, "PersistenceScenario");
            }
            var captures = new ConcurrentQueue<(string Path, string Header, string Body)>();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            await using var recorder = builder.Build();
            recorder.Map("/{**path}", async (HttpContext c) =>
            {
                using var reader = new StreamReader(c.Request.Body);
                captures.Enqueue((c.Request.Path.Value!, c.Request.Headers["X-Synthetic"].ToString(), await reader.ReadToEndAsync(token)));
                c.Response.StatusCode = 204;
            });
            await recorder.StartAsync(token);
            var origin = recorder.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var data = new TemporaryStorage();
            CameraEdit Camera(bool enabled = true) => new("persisted", "Synthetic persisted", endpoint.AbsoluteUri, enabled,
                Replace("synthetic-user"), Replace("synthetic-password"));
            var condition = new FieldCondition(FieldSection.Data, "State", "true", ComparisonMode.Boolean);
            // State is the documented simulator fallback, never a production assumption.
            ForwardingRule Match(string id, OccurrenceMode mode) => new(id, "persisted", topic, condition, true, mode, TimeSpan.FromHours(1));
            RuleEdit Rule(string id, OccurrenceMode mode) => new(Match(id, mode), "Synthetic persisted rule", "POST",
                url: Replace(origin + "/" + id), headers: Replace(new[] { new ActionHeader("X-Synthetic", secret) }),
                jsonBody: Replace("{\"synthetic\":\"" + secret + "\"}"));
            // Seed config with workers stopped, then start two entirely fresh hosts from disk.
            var seed = new ConfigurationStore(data.Path, Pipeline());
            seed.Initialize();
            seed.Save(0, [Camera(), new("disabled", "Synthetic disabled", endpoint.AbsoluteUri, false)],
                [Rule("occurrence", OccurrenceMode.Occurrence), Rule("transition", OccurrenceMode.Transition)]);
            for (var pass = 0; pass < 2; pass++)
            {
                var hostBuilder = Host.CreateApplicationBuilder();
                hostBuilder.Logging.ClearProviders();
                hostBuilder.Services.AddPersistentCameraPipeline(data.Path);
                using var host = hostBuilder.Build();
                var pipeline = host.Services.GetRequiredService<CameraPipeline>();
                var history = host.Services.GetRequiredService<EventHistory>();
                var supervisor = host.Services.GetServices<IHostedService>().OfType<CameraSupervisor>().Single();
                Check(pipeline.Cameras.Count == 0 && history.Snapshot().Count == 0 && pipeline.Status.Count == 0, "FreshRuntime");
                try
                {
                    await host.StartAsync(token);
                    await Until(() => supervisor.Status.Single(s => s.CameraId == "persisted").State == "Subscribed");
                    Check(supervisor.Status.Single(s => s.CameraId == "disabled").State == "Disabled", "DisabledRestored");
                    var count = captures.Count;
                    // First true after restart establishes a new transition baseline, and occurrence cooldown is empty.
                    await Trigger(true);
                    await Until(() => captures.Count == count + 1 && pipeline.LastExecutions.Count == 1);
                    Check(captures.Last().Path == "/occurrence", "NoRestoredTransitionOrCooldown");
                    await Trigger(false);
                    await Until(() => history.Snapshot().Any(e => e.Topic == topic && e.Data.Any(f => f.Value == "false")));
                    await Trigger(true);
                    await Until(() => captures.Count == count + 2 && pipeline.LastExecutions.Count == 2);
                    Check(captures.Last().Path == "/transition" && captures.All(c => c.Header == secret && c.Body.Contains(secret, StringComparison.Ordinal)), "RestoredActionSecrets");
                    var store = host.Services.GetRequiredService<ConfigurationStore>();
                    var current = pipeline.Cameras.Single(c => c.Id == "persisted");
                    using (var connection = data.Open())
                    { Execute(connection, "CREATE TRIGGER FailSave BEFORE INSERT ON Cameras BEGIN SELECT RAISE(ABORT,'synthetic'); END"); }
                    Throws<StorageException>(() => store.Save(1 + pass * 2, [Camera(false)],
                        [Rule("occurrence", OccurrenceMode.Occurrence), Rule("transition", OccurrenceMode.Transition)]));
                    Check(ReferenceEquals(current, pipeline.Cameras.Single(c => c.Id == "persisted")), "FailedSavePreservesWorker");
                    using (var connection = data.Open()) { Execute(connection, "DROP TRIGGER FailSave"); }
                    var revision = store.Save(1 + pass * 2, [Camera(false)],
                        [Rule("occurrence", OccurrenceMode.Occurrence), Rule("transition", OccurrenceMode.Transition)]);
                    await Until(() => supervisor.Status.Single().State == "Disabled");
                    Check(!pipeline.ManualTest("occurrence"), "CommittedDisable");
                    store.Save(revision, [Camera(), new("disabled", "Synthetic disabled", endpoint.AbsoluteUri, false)],
                        [Rule("occurrence", OccurrenceMode.Occurrence), Rule("transition", OccurrenceMode.Transition)]);
                    await Until(() => supervisor.Status.Single(s => s.CameraId == "persisted").State == "Subscribed");
                }
                finally
                {
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await host.StopAsync(shutdown.Token);
                }
            }
            await recorder.StopAsync(token);
            Console.WriteLine("PASS: persisted restart reconciliation and restored camera-to-recorder forwarding.");

            async Task Trigger(bool value)
            {
                using var content = JsonContent.Create(new Trigger(topic, value, 0), SimulatorJson.Default.Trigger);
                await content.LoadIntoBufferAsync(token);
                using var response = await control.PostAsync("api/cameras/synthetic-camera/events", content, token);
                Check(response.IsSuccessStatusCode, "PersistenceTrigger");
            }
            async Task Until(Func<bool> condition)
            { while (!condition()) { await Task.Delay(20, token); } }
        }
    }
}
