namespace OnvifWebhookBridge.Tests.Web
{
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using OnvifWebhookBridge.Web;
    using System.Net;

    internal static class HealthPath
    {
        public static async Task Verify(CancellationToken token)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            var scenario = "healthy";
            var redirectFollowed = false;
            app.MapGet("/redirect", () =>
            {
                redirectFollowed = true;
                return Results.Text("Healthy");
            });
            app.MapGet("/health", async (HttpContext context) =>
            {
                if (scenario == "redirect")
                {
                    context.Response.Redirect("/redirect");
                    return;
                }
                if (scenario == "status")
                {
                    context.Response.StatusCode = 503;
                }
                await context.Response.WriteAsync("Healthy", token);
                await context.Response.Body.FlushAsync(token);
                if (scenario == "oversized")
                {
                    // No Content-Length: enforce the bound while streaming, too.
                    await context.Response.WriteAsync("extra", token);
                    await context.Response.Body.FlushAsync(token);
                }
                if (scenario is "stalled" or "oversized")
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                    }
                }
            });
            await app.StartAsync(token);
            var port = new Uri(SecurityPath.Origin(app)).Port.ToString();
            foreach (var value in new[] { "healthy", "oversized", "redirect", "status", "stalled" })
            {
                scenario = value;
                var result = HealthProbe.Run(port);
                // An oversized body must be rejected before the four-second deadline,
                // even when the peer never finishes sending the response.
                var exit = value == "oversized" ? await result.WaitAsync(TimeSpan.FromSeconds(3), token) : await result;
                if (exit != (value == "healthy" ? 0 : 1))
                {
                    throw new InvalidOperationException("Health probe response boundary failed.");
                }
            }
            if (redirectFollowed)
            {
                throw new InvalidOperationException("Health probe followed a redirect.");
            }
            await app.StopAsync(token);
        }
    }
}
