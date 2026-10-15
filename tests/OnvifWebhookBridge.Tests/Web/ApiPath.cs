namespace OnvifWebhookBridge.Tests.Web
{
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Rules;
    using OnvifWebhookBridge.Tests.Onvif;
    using OnvifWebhookBridge.Tests.Persistence;
    using OnvifWebhookBridge.Web;
    using System.Collections.Concurrent;
    using System.Net;
    using System.Net.Http.Json;
    using static OnvifWebhookBridge.Tests.Persistence.StoragePath;
    using static SecurityPath;

    internal static class ApiPath
    {
        public static async Task Verify(Uri endpoint, Uri controlAddress, CancellationToken token)
        {
            const string topic = "Synthetic/Phase6/Detection";
            using var control = new HttpClient { BaseAddress = controlAddress, Timeout = TimeSpan.FromSeconds(10) };
            var scenario = new Scenario(1, new(IPAddress.Any.ToString(), IPAddress.Any.ToString(), false),
                [new("synthetic-camera", "Synthetic API camera", "generic", [new("synthetic-user", "synthetic-password", "Administrator")],
                    new(IPAddress.Any.ToString(), 8000, endpoint.Host, endpoint.Port))]);
            using (var content = JsonContent.Create(scenario, SimulatorJson.Default.Scenario))
            {
                await content.LoadIntoBufferAsync(token);
                using var response = await control.PostAsync("api/scenario", content, token);
                Check(response.IsSuccessStatusCode, "ApiScenario");
            }
            var captures = new ConcurrentQueue<string>();
            var b = WebApplication.CreateSlimBuilder();
            b.Logging.ClearProviders();
            b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            await using var recorder = b.Build();
            recorder.MapPost("/synthetic", async (HttpContext c) =>
            {
                using var reader = new StreamReader(c.Request.Body);
                captures.Enqueue(c.Request.Headers["X-Synthetic"].ToString() + await reader.ReadToEndAsync(token));
                c.Response.StatusCode = 204;
            });
            await recorder.StartAsync(token);
            using var data = new TemporaryStorage();
            // Administrator only is bootstrapped in process. Every configuration edit uses HTTP.
            var seed = new ConfigurationStore(data.Path, Pipeline());
            seed.Initialize();
            seed.BootstrapAdministrator("synthetic-admin", "synthetic-admin-password");
            string? previousCookie = null;
            for (var pass = 0; pass < 2; pass++)
            {
                await using var app = await Host(data.Path, token);
                using var client = Client(app);
                var origin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
                var session = await Login(client);
                if (previousCookie is not null)
                {
                    using var stale = await Send(client, "GET", "/api/session", cookie: previousCookie);
                    Check(stale.StatusCode == HttpStatusCode.Unauthorized, "RestartRevokesSessions");
                }
                previousCookie = session.Cookie;
                var revision = pass == 0 ? 0L : 5L;
                if (pass == 0)
                {
                    await Ok("PUT", "/api/cameras/api-camera", new CameraRequest(revision++, "Synthetic API", endpoint.AbsoluteUri, true,
                        new(SecretOperation.Replace, "synthetic-user"), new(SecretOperation.Replace, "synthetic-password")));
                    await Ok("PUT", "/api/rules/api-rule", new RuleRequest(revision++, "Synthetic API action", "api-camera", topic,
                        new(FieldSection.Data, "State", "true", ComparisonMode.Boolean), true, Method: "POST",
                        Url: new(SecretOperation.Replace, Origin(recorder) + "/synthetic"),
                        Headers: new(SecretOperation.Replace, [new("X-Synthetic", "synthetic-api-token")]),
                        JsonBody: new(SecretOperation.Replace, "{\"synthetic\":true}")));
                    // State is the actual simulator's fallback field, not a core assumption.
                }
                await Until(async () => (await Get("/api/camera-status")).Contains("Subscribed", StringComparison.Ordinal));
                using (var result = await Send(client, "POST", "/api/cameras/api-camera/test", cookie: session.Cookie, csrf: session.Csrf, origin: origin))
                {
                    Check(result.IsSuccessStatusCode && (await result.Content.ReadAsStringAsync(token)).Contains("\"reachable\":true", StringComparison.Ordinal), "BoundedConnectivity");
                }
                var count = captures.Count;
                await Trigger(false);
                await Until(async () => (await Get("/api/events")).Contains("false", StringComparison.Ordinal));
                await Trigger(true);
                await Until(() => Task.FromResult(captures.Count == count + 1));
                Check(captures.Last() == "synthetic-api-token{\"synthetic\":true}", "ApiEventToRecorder");
                var masked = await Get("/api/configuration");
                Check(!masked.Contains("synthetic-api-token", StringComparison.Ordinal) && !masked.Contains("synthetic-password", StringComparison.Ordinal) && !masked.Contains(Origin(recorder), StringComparison.Ordinal), "ApiMaskedSecrets");
                using (var manual = await Send(client, "POST", "/api/rules/api-rule/test", cookie: session.Cookie, csrf: session.Csrf, origin: origin))
                {
                    Check(manual.StatusCode == HttpStatusCode.Accepted, "ExplicitManualTest");
                }
                await Until(() => Task.FromResult(captures.Count == count + 2));
                await Until(async () => (await Get("/api/executions")).Contains("204", StringComparison.Ordinal));
                if (pass == 0)
                {
                    await Ok("POST", "/api/cameras/api-camera/enabled", new EnabledRequest(revision++, false));
                    await Until(async () => (await Get("/api/camera-status")).Contains("Disabled", StringComparison.Ordinal));
                    using (var manual = await Send(client, "POST", "/api/rules/api-rule/test", cookie: session.Cookie, csrf: session.Csrf, origin: origin))
                    {
                        Check(manual.StatusCode == HttpStatusCode.Conflict, "DisabledCameraTestRejected");
                    }
                    await Ok("POST", "/api/cameras/api-camera/enabled", new EnabledRequest(revision++, true));
                    await Until(async () => (await Get("/api/camera-status")).Contains("Subscribed", StringComparison.Ordinal));
                    await Ok("PUT", "/api/cameras/api-camera", new CameraRequest(revision++, "Synthetic API edited", endpoint.AbsoluteUri, true));
                    await Until(async () => (await Get("/api/camera-status")).Contains("Subscribed", StringComparison.Ordinal));
                }
                else
                {
                    using (var referenced = await Send(client, "DELETE", "/api/cameras/api-camera", new RevisionRequest(revision), session.Cookie, session.Csrf, origin))
                    {
                        Check(referenced.StatusCode == HttpStatusCode.Conflict, "ReferencedCamera");
                    }
                    await Ok("POST", "/api/rules/api-rule/enabled", new EnabledRequest(revision++, false));
                    await Ok("DELETE", "/api/rules/api-rule", new RevisionRequest(revision++));
                    await Ok("DELETE", "/api/cameras/api-camera", new RevisionRequest(revision++));
                    await Until(async () => await Get("/api/camera-status") == "[]");
                }
                await app.StopAsync(token);
                async Task<string> Get(string path)
                {
                    using var response = await Send(client, "GET", path, cookie: session.Cookie);
                    Check(response.IsSuccessStatusCode, "ApiRead");
                    return await response.Content.ReadAsStringAsync(token);
                }
                async Task Ok(string method, string path, object body)
                {
                    using var response = await Send(client, method, path, body, session.Cookie, session.Csrf, origin);
                    Check(response.IsSuccessStatusCode, "ApiMutation" + method + path);
                }
            }
            await recorder.StopAsync(token);
            Console.WriteLine("PASS: authenticated API CRUD/reconciliation, persisted restart, simulator events and explicit local-recorder testing.");
            async Task Trigger(bool state)
            {
                using var content = JsonContent.Create(new Trigger(topic, state, 0), SimulatorJson.Default.Trigger);
                await content.LoadIntoBufferAsync(token);
                using var response = await control.PostAsync("api/cameras/synthetic-camera/events", content, token);
                Check(response.IsSuccessStatusCode, "ApiTrigger");
            }
            async Task Until(Func<Task<bool>> condition)
            {
                while (!await condition())
                {
                    await Task.Delay(25, token);
                }
            }
        }
    }
}
