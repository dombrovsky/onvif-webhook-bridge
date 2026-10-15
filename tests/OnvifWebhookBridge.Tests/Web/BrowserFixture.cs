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

    // Explicit test-executable mode only; never registered in the production application.
    internal static class BrowserFixture
    {
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
