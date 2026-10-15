namespace OnvifWebhookBridge.Tests.Web
{
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Tests.Persistence;
    using OnvifWebhookBridge.Web;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Sockets;
    using static OnvifWebhookBridge.Tests.Persistence.StoragePath;

    internal static class ExecutablePath
    {
        public static async Task Verify(CancellationToken token)
        {
            using var data = new TemporaryStorage();
            var native = Environment.GetEnvironmentVariable("ONVIF_TEST_BRIDGE_EXECUTABLE");
            var executable = native ?? Path.Combine(AppContext.BaseDirectory, "OnvifWebhookBridge.dll");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var origin = new UriBuilder("http", IPAddress.Loopback.ToString(), port).Uri;
            Process Start(bool reset, string? username, string? password)
            {
                var info = new ProcessStartInfo(native is null ? "dotnet" : executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (native is null)
                {
                    info.ArgumentList.Add(executable);
                }
                if (reset)
                {
                    info.ArgumentList.Add("--reset-administrator");
                }
                info.Environment["ONVIF_DATA_DIRECTORY"] = data.Path;
                info.Environment.Remove("ONVIF_ADMIN_PASSWORD_FILE");
                info.Environment.Remove("ONVIF_ADMIN_USERNAME");
                info.Environment.Remove("ONVIF_ADMIN_PASSWORD");
                if (username is not null)
                {
                    info.Environment["ONVIF_ADMIN_USERNAME"] = username;
                }
                if (password is not null)
                {
                    info.Environment["ONVIF_ADMIN_PASSWORD"] = password;
                }
                info.Environment["ASPNETCORE_URLS"] = origin.GetLeftPart(UriPartial.Authority);
                return Process.Start(info) ?? throw new InvalidOperationException();
            }
            using (var unset = Start(false, null, null))
            {
                await unset.WaitForExitAsync(token);
                Check(unset.ExitCode == 1, "ExecutableFailsClosedWithoutAdministrator");
            }
            using (var reset = Start(true, "synthetic-admin", "synthetic-admin-password"))
            {
                await reset.WaitForExitAsync(token);
                Check(reset.ExitCode == 0, "OfflineResetExit");
                Check(string.IsNullOrWhiteSpace(await reset.StandardError.ReadToEndAsync(token)), "ResetSafeOutput");
            }
            // Environment changes must not replace the established administrator on normal start.
            using (var server = Start(false, "other-synthetic-admin", "other-synthetic-password"))
            {
                var output = server.StandardOutput.ReadToEndAsync(token);
                var errors = server.StandardError.ReadToEndAsync(token);
                try
                {
                    using var client = new HttpClient(new HttpClientHandler { UseCookies = false, UseProxy = false }) { BaseAddress = origin };
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        Check(!server.HasExited, "ExecutableRemainsRunning");
                        try
                        {
                            using var response = await client.GetAsync("health", token);
                            if (response.IsSuccessStatusCode)
                            {
                                break;
                            }
                        }
                        catch (HttpRequestException) { }
                        await Task.Delay(50, token);
                    }
                    await BrowserPath.Verify(client, token);
                    var session = await SecurityPath.Login(client);
                    using var denied = await SecurityPath.Send(client, "GET", "/api/configuration");
                    Check(denied.StatusCode == HttpStatusCode.Unauthorized, "ExecutableProtected");
                    using var allowed = await SecurityPath.Send(client, "GET", "/api/configuration", cookie: session.Cookie);
                    Check(allowed.IsSuccessStatusCode, "ExecutableJson");
                }
                finally { if (!server.HasExited) { server.Kill(entireProcessTree: true); } await server.WaitForExitAsync(CancellationToken.None); }
                Check(!(await output).Contains("synthetic-admin-password", StringComparison.Ordinal) &&
                    !(await errors).Contains("synthetic-password", StringComparison.Ordinal), "ExecutableRedaction");
            }
            using (var reset = Start(true, "replacement-admin", "replacement-admin-password"))
            {
                await reset.WaitForExitAsync(token);
                Check(reset.ExitCode == 0, "ExecutableReset");
            }
            var store = new ConfigurationStore(data.Path, Pipeline());
            store.Initialize();
            Check(store.Read().Revision == 0 && !store.VerifyAdministrator("synthetic-admin", "synthetic-admin-password") &&
                store.VerifyAdministrator("replacement-admin", "replacement-admin-password"), "ExecutableResetPersists");
            Console.WriteLine("PASS: production executable offline reset, bootstrap preservation, authenticated JSON and safe output.");
        }
    }
}
