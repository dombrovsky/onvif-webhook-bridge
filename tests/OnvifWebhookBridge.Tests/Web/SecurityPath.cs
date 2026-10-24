namespace OnvifWebhookBridge.Tests.Web
{
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Hosting.Server;
    using Microsoft.AspNetCore.Hosting.Server.Features;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Tests.Persistence;
    using OnvifWebhookBridge.Web;
    using System.Net;
    using System.Net.Http.Json;
    using System.Text;
    using System.Text.Json;
    using static OnvifWebhookBridge.Tests.Persistence.StoragePath;

    internal static class SecurityPath
    {
        internal static async Task<WebApplication> Host(string directory, CancellationToken token)
        {
            var b = WebApplication.CreateSlimBuilder();
            b.Logging.ClearProviders();
            b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            b.Services.AddPersistentCameraPipeline(directory).AddAdministration();
            var app = b.Build();
            app.MapAdministration();
            await app.StartAsync(token);
            app.Services.GetRequiredService<CameraPipeline>().SetSelfOrigins([new Uri(Origin(app))]);
            return app;
        }
        internal static string Origin(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        internal static HttpClient Client(WebApplication app) => new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(Origin(app)) };
        internal static async Task<HttpResponseMessage> Send(HttpClient client, string method, string path, object? body = null,
            string? cookie = null, string? csrf = null, string? origin = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }
            if (cookie is not null)
            {
                request.Headers.Add("Cookie", cookie);
            }
            if (csrf is not null)
            {
                request.Headers.Add("X-CSRF-Token", csrf);
            }
            if (body is not null)
            {
                var info = JsonContext.Default.GetTypeInfo(body.GetType()) ?? throw new InvalidOperationException();
                request.Content = new StringContent(JsonSerializer.Serialize(body, info), Encoding.UTF8, "application/json");
            }
            return await client.SendAsync(request);
        }
        internal static async Task<(string Cookie, string Csrf)> Login(HttpClient client)
        {
            using var response = await Send(client, "POST", "/api/login", new LoginRequest("synthetic-admin", "synthetic-admin-password"), origin: client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            Check(response.StatusCode == HttpStatusCode.OK, "Login");
            var cookie = response.Headers.GetValues("Set-Cookie").Single();
            Check(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase) && cookie.Contains("path=/", StringComparison.OrdinalIgnoreCase), "CookieProtections");
            var session = await response.Content.ReadFromJsonAsync(JsonContext.Default.SessionView) ?? throw new InvalidOperationException();
            Check(session.Version == ApplicationVersion.Current, "AuthenticatedCompiledVersion");
            return (cookie.Split(';')[0], session.CsrfToken);
        }
        public static async Task Verify(CancellationToken token)
        {
            using var data = new TemporaryStorage();
            await using var app = await Host(data.Path, token);
            using var client = Client(app);
            var origin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
            foreach (var path in new[] { "/api/configuration", "/api/cameras", "/api/rules", "/api/events", "/api/camera-status", "/api/executions", "/api/execution-status", "/api/session" })
            {
                using var denied = await Send(client, "GET", path);
                Check(denied.StatusCode == HttpStatusCode.Unauthorized, "ProtectedRead");
            }
            foreach (var path in new[] { "/api/logout", "/api/cameras/synthetic/test", "/api/rules/synthetic/test", "/api/cameras/synthetic/enabled", "/api/rules/synthetic/enabled" })
            {
                using var denied = await Send(client, "POST", path, origin: origin);
                Check(denied.StatusCode == HttpStatusCode.Unauthorized, "ProtectedMutation");
            }
            foreach (var path in new[] { "/api/cameras/synthetic", "/api/rules/synthetic" })
            {
                foreach (var method in new[] { "PUT", "DELETE" })
                {
                    using var denied = await Send(client, method, path, origin: origin);
                    Check(denied.StatusCode == HttpStatusCode.Unauthorized, "ProtectedCrud");
                }
            }

            using (var health = await client.GetAsync("/health", token))
            {
                Check(await health.Content.ReadAsStringAsync(token) == "Healthy", "LivenessOnly");
            }
            var auth = app.Services.GetRequiredService<AdminAuthentication>();
            auth.Bootstrap(_ => null);
            Check(!app.Services.GetRequiredService<ConfigurationStore>().Read().HasAdministrator, "NoDefault");
            var passwordFile = Path.Combine(data.Path, "bootstrap-password");
            File.WriteAllText(passwordFile, "synthetic-admin-password\n");
            auth.Bootstrap(name => name switch { "ONVIF_ADMIN_USERNAME" => "synthetic-admin", "ONVIF_ADMIN_PASSWORD_FILE" => passwordFile, _ => null });
            File.Delete(passwordFile);
            // Established administrator ignores stale or missing environment/password files.
            auth.Bootstrap(_ => "synthetic-unavailable");
            var session = await Login(client);
            foreach (var test in new[] { ((string?)null, session.Csrf), ("https://foreign.example", session.Csrf), (origin, (string?)null), (origin, "invalid") })
            {
                using var denied = await Send(client, "PUT", "/api/cameras/synthetic", new CameraRequest(0, "Synthetic", "https://camera.example/device"), session.Cookie, test.Item2, test.Item1);
                Check(denied.StatusCode == HttpStatusCode.Forbidden, "CsrfAndOrigin");
            }
            using (var response = await Send(client, "POST", "/api/login", new LoginRequest("synthetic-admin", "synthetic-admin-password"), origin: "https://foreign.example"))
            {
                Check(response.StatusCode == HttpStatusCode.Forbidden, "LoginCsrf");
            }
            using (var save = await Send(client, "PUT", "/api/cameras/synthetic", new CameraRequest(0, "<script>synthetic</script>", "https://camera.example/device", false,
                new(SecretOperation.Replace, "synthetic-user"), new(SecretOperation.Replace, "synthetic-camera-password")), session.Cookie, session.Csrf, origin))
            {
                Check(save.StatusCode == HttpStatusCode.OK, "CameraCreate");
            }
            using (var get = await Send(client, "GET", "/api/configuration", cookie: session.Cookie))
            {
                var json = await get.Content.ReadAsStringAsync(token);
                Check(!json.Contains("synthetic-camera-password", StringComparison.Ordinal) && !json.Contains("synthetic-user", StringComparison.Ordinal) && !json.Contains("<script>", StringComparison.Ordinal), "MaskedEscapedJson");
            }
            using (var conflict = await Send(client, "PUT", "/api/cameras/synthetic", new CameraRequest(0, "Synthetic", "https://camera.example/device"), session.Cookie, session.Csrf, origin))
            {
                Check(conflict.StatusCode == HttpStatusCode.Conflict && (await conflict.Content.ReadAsStringAsync(token)).Contains("RevisionConflict", StringComparison.Ordinal), "RevisionConflict");
            }
            using (var invalid = await Send(client, "PUT", "/api/cameras/synthetic", new CameraRequest(1, "Synthetic", "invalid-synthetic-secret"), session.Cookie, session.Csrf, origin))
            {
                Check(invalid.StatusCode == HttpStatusCode.BadRequest && !(await invalid.Content.ReadAsStringAsync(token)).Contains("invalid-synthetic-secret", StringComparison.Ordinal), "SafeValidation");
            }
            using (var connection = data.Open())
            {
                Execute(connection, "CREATE TRIGGER FailApiSave BEFORE INSERT ON Cameras BEGIN SELECT RAISE(ABORT,'synthetic-database-secret'); END");
            }
            using (var failed = await Send(client, "PUT", "/api/cameras/synthetic", new CameraRequest(1, "changed", "https://camera.example/device"), session.Cookie, session.Csrf, origin))
            {
                Check(failed.StatusCode == HttpStatusCode.ServiceUnavailable && !(await failed.Content.ReadAsStringAsync(token)).Contains("synthetic-database-secret", StringComparison.Ordinal), "SafeFailedCommit");
            }
            Check(app.Services.GetRequiredService<ConfigurationStore>().Read().Revision == 1 && app.Services.GetRequiredService<CameraPipeline>().Cameras.Single().Name == "<script>synthetic</script>", "CommitBeforeApply");
            using (var connection = data.Open())
            {
                Execute(connection, "DROP TRIGGER FailApiSave");
            }
            using (var self = await Send(client, "PUT", "/api/cameras/synthetic", new CameraRequest(1, "Synthetic", origin), session.Cookie, session.Csrf, origin))
            {
                Check(self.StatusCode == HttpStatusCode.BadRequest, "SelfCameraRejected");
            }
            using (var rule = await Send(client, "PUT", "/api/rules/synthetic-rule", new RuleRequest(1, "Synthetic", "synthetic", "Synthetic/Topic",
                Url: new(SecretOperation.Replace, "https://destination.example/action"),
                Headers: new(SecretOperation.Replace, [new("X-Synthetic", "synthetic-header-secret")])), session.Cookie, session.Csrf, origin))
            {
                Check(rule.StatusCode == HttpStatusCode.OK, "SecretRuleCreate");
            }
            using (var clear = await Send(client, "PUT", "/api/rules/synthetic-rule", new RuleRequest(2, "Synthetic", "synthetic", "Synthetic/Topic",
                Headers: new(SecretOperation.Clear)), session.Cookie, session.Csrf, origin))
            {
                Check(clear.StatusCode == HttpStatusCode.OK, "ExplicitSecretClear");
            }
            Check(app.Services.GetRequiredService<ConfigurationStore>().Read().Rules.Single() is { HasHeaders: false, HasUrl: true }, "PreserveAndClear");
            foreach (var payload in new[] { "{}", "{\"revision\":3,\"name\":null,\"endpoint\":\"https://camera.example/device\"}",
                "{\"revision\":3,\"name\":\"Synthetic\",\"endpoint\":\"https://camera.example/device\",\"unexpected\":true}" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, "/api/cameras/synthetic");
                request.Headers.Add("Cookie", session.Cookie);
                request.Headers.Add("Origin", origin);
                request.Headers.Add("X-CSRF-Token", session.Csrf);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, token);
                Check(response.StatusCode == HttpStatusCode.BadRequest, "StrictJsonContract");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Put, "/api/cameras/synthetic"))
            {
                request.Headers.Add("Cookie", session.Cookie);
                request.Headers.Add("Origin", origin);
                request.Headers.Add("X-CSRF-Token", session.Csrf);
                request.Content = new StringContent(new string('x', 262145), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, token);
                Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "BoundedRequest");
            }
            async Task<HttpStatusCode> SetVisibility(long revision, bool? sensitive, StringSecret? url = null)
            {
                using var response = await Send(client, "PUT", "/api/rules/synthetic-rule",
                    new RuleRequest(revision, "Synthetic", "synthetic", "Synthetic/Topic", Url: url, UrlSensitive: sensitive),
                    session.Cookie, session.Csrf, origin);
                return response.StatusCode;
            }
            Check(await SetVisibility(3, false) == HttpStatusCode.BadRequest, "ApiCannotRevealStoredUrl");
            Check(await SetVisibility(3, false, new(SecretOperation.Replace, "https://destination.example/public-synthetic")) == HttpStatusCode.OK, "ApiPublicUrlOptIn");
            foreach (var path in new[] { "/api/configuration", "/api/rules" })
            {
                using var response = await Send(client, "GET", path, cookie: session.Cookie);
                Check((await response.Content.ReadAsStringAsync(token)).Contains("https://destination.example/public-synthetic", StringComparison.Ordinal), "AuthenticatedPublicUrlRead");
                using var denied = await Send(client, "GET", path);
                Check(denied.StatusCode == HttpStatusCode.Unauthorized, "PublicUrlStillRequiresAuthentication");
            }
            using (var enabled = await Send(client, "POST", "/api/rules/synthetic-rule/enabled", new EnabledRequest(4, false), session.Cookie, session.Csrf, origin))
            {
                Check(enabled.IsSuccessStatusCode && !app.Services.GetRequiredService<ConfigurationStore>().Read().Rules.Single().UrlSensitive, "VisibilitySurvivesEnabledEdit");
            }
            Check(await SetVisibility(5, true) == HttpStatusCode.OK, "ApiMarkUrlSensitive");
            using (var response = await Send(client, "GET", "/api/configuration", cookie: session.Cookie))
            {
                Check(!(await response.Content.ReadAsStringAsync(token)).Contains("public-synthetic", StringComparison.Ordinal), "ApiSensitiveUrlNotReturned");
            }
            Check(await SetVisibility(6, null) == HttpStatusCode.OK && app.Services.GetRequiredService<ConfigurationStore>().Read().Rules.Single().UrlSensitive,
                "OmittedClassificationPreservesSecret");
            using (var logout = await Send(client, "POST", "/api/logout", cookie: session.Cookie, csrf: session.Csrf, origin: origin))
            {
                Check(logout.StatusCode == HttpStatusCode.NoContent, "Logout");
            }
            using (var expired = await Send(client, "GET", "/api/session", cookie: session.Cookie))
            {
                Check(expired.StatusCode == HttpStatusCode.Unauthorized, "RevokedSession");
            }
            await app.StopAsync(token);
            Console.WriteLine("PASS: authenticated API, bootstrap, CSRF/origin, masked JSON, revision and failed-commit boundaries.");
        }
    }
}
