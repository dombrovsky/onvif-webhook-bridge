namespace OnvifWebhookBridge.Tests.Web
{
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Web;
    using System.Net;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;

    // Explicit test-executable mode only; never registered in the production application.
    internal static class BrowserFixture
    {
        public static async Task RunRecorder()
        {
            var count = 0;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Synthetic test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText("/tmp/synthetic-ca.pem", ca.ExportCertificatePem());
            using var serverKey = RSA.Create(2048);
            var serverRequest = new CertificateRequest("CN=recorder", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("recorder");
            serverRequest.CertificateExtensions.Add(names.Build());
            using var signed = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
            using var certificate = signed.CopyWithPrivateKey(serverKey);
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Any, 8081);
                o.Listen(IPAddress.Any, 8443, endpoint => endpoint.UseHttps(certificate));
            });
            await using var app = builder.Build();
            app.MapMethods("/synthetic", ["GET", "POST"], (HttpContext context) =>
            {
                Interlocked.Increment(ref count);
                context.Response.StatusCode = 204;
            });
            app.MapGet("/count", () => Results.Text(Volatile.Read(ref count).ToString()));
            await app.RunAsync();
        }

        public static async Task Run()
        {
            var directory = Environment.GetEnvironmentVariable("ONVIF_BROWSER_DATA") ?? throw new InvalidOperationException();
            var clock = new BrowserClock();
            var count = 0;
            var recorderBuilder = WebApplication.CreateSlimBuilder();
            recorderBuilder.Logging.ClearProviders();
            recorderBuilder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, int.Parse(Environment.GetEnvironmentVariable("ONVIF_BROWSER_RECORDER_PORT") ?? "8081")));
            await using var recorder = recorderBuilder.Build();
            recorder.MapMethods("/synthetic", ["GET", "POST"], (HttpContext c) =>
            {
                Interlocked.Increment(ref count);
                c.Response.StatusCode = 204;
            });
            await recorder.StartAsync();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, int.Parse(Environment.GetEnvironmentVariable("ONVIF_BROWSER_PORT") ?? "8080")));
            builder.Services.AddPersistentCameraPipeline(directory).AddAdministration();
            builder.Services.AddSingleton<TimeProvider>(clock);
            await using var app = builder.Build();
            var store = app.Services.GetRequiredService<ConfigurationStore>();
            store.Initialize();
            store.BootstrapAdministrator("synthetic-admin", "synthetic-admin-password");
            app.MapAdministration();
            app.MapGet("/fixture/recorder", () => Results.Text(SecurityPath.Origin(recorder) + "/synthetic"));
            app.MapGet("/fixture/count", () => Results.Text(Volatile.Read(ref count).ToString()));
            app.MapPost("/fixture/expire", () =>
            {
                clock.Offset = TimeSpan.FromHours(9);
                return Results.NoContent();
            });
            await app.RunAsync();
        }
        private sealed class BrowserClock : TimeProvider
        {
            public TimeSpan Offset;
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
        }
    }
}
