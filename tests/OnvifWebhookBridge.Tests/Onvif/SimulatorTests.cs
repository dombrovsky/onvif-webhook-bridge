namespace OnvifWebhookBridge.Tests.Onvif
{
    using System.Net;
    using DotNet.Testcontainers.Builders;
    using Xunit;

    public sealed class SimulatorTests
    {
        [Fact]
        [Trait("Category", "Unit")]
        public void SharedNativeSyntheticContentChecks() => SimulatorPath.VerifySyntheticContent();

        [Fact]
        [Trait("Category", "Integration")]
        public async Task SubscriptionReceivesTriggeredStatesAfterRenewalThroughNormalizationAndAdmission()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await using var simulator = new ContainerBuilder("onvif-bridge-tests/onvifsim:8f86706c2ba768006e85b38f6bc1c760d36f1bba")
                .WithPortBinding(8000, true)
                .WithPortBinding(9000, true)
                .WithCommand("--cameras", "1", "--bind", IPAddress.Any.ToString(),
                    "--control-bind", IPAddress.Any.ToString(), "--no-discovery")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                    .ForPort(9000).ForPath("/api/cameras")))
                .Build();
            await simulator.StartAsync(deadline.Token);
            await SimulatorPath.Verify(
                new UriBuilder("http", simulator.Hostname, simulator.GetMappedPublicPort(8000), "/onvif/device_service").Uri,
                new UriBuilder("http", simulator.Hostname, simulator.GetMappedPublicPort(9000)).Uri,
                deadline.Token);
        }
    }
}
