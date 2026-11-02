namespace OnvifWebhookBridge.Tests.Dispatch
{
    using DotNet.Testcontainers.Builders;
    using System.Text;
    using Xunit;

    [Collection("Metrics"), Trait("Category", "Integration")]
    public sealed class TelegrafTests
    {
        [Fact]
        public async Task GenericAndSignalSpecificGrpcExportArrivesAtPinnedTelegraf()
        {
            using var stream = typeof(TelegrafTests).Assembly.GetManifestResourceStream("Telegraf.conf")!;
            using var reader = new StreamReader(stream);
            await using var collector = new ContainerBuilder(TelegrafPath.Image)
                .WithResourceMapping(Encoding.UTF8.GetBytes(await reader.ReadToEndAsync()), "/etc/telegraf/telegraf.conf")
                .WithPortBinding(4317, true).WithPortBinding(9273, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(9273).ForPath("/metrics")))
                .Build();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await collector.StartAsync(deadline.Token);
            await TelegrafPath.Verify(new UriBuilder("http", collector.Hostname, collector.GetMappedPublicPort(4317)).Uri,
                new UriBuilder("http", collector.Hostname, collector.GetMappedPublicPort(9273), "/metrics").Uri, deadline.Token);
        }
    }
}
