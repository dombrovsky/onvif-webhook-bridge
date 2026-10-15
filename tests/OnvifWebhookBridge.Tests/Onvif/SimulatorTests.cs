namespace OnvifWebhookBridge.Tests.Onvif
{
    using System.Net;
    using System.Net.Http.Json;
    using System.Xml;
    using DotNet.Testcontainers.Builders;
    using OnvifWebhookBridge.Onvif;
    using SharpOnvifClient;
    using SharpOnvifClient.Events;
    using SharpOnvifCommon.Security;
    using SharpOnvifCommon.Soap;
    using Xunit;

    public sealed class SimulatorTests
    {
        [Fact]
        [Trait("Category", "Integration")]
        public async Task SubscriptionReceivesTriggeredTrueAndFalseAfterRenewal()
        {
            const string username = "synthetic-user";
            const string password = "synthetic-password";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var token = deadline.Token;
            await using var simulator = new ContainerBuilder("onvif-bridge-tests/onvifsim:8f86706c2ba768006e85b38f6bc1c760d36f1bba")
                .WithPortBinding(8000, true)
                .WithPortBinding(9000, true)
                .WithCommand("--cameras", "1", "--bind", IPAddress.Any.ToString(),
                    "--control-bind", IPAddress.Any.ToString(), "--no-discovery")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                    .ForPort(9000).ForPath("/api/cameras")))
                .Build();
            await simulator.StartAsync(token);

            using var control = new HttpClient
            {
                BaseAddress = new UriBuilder("http", simulator.Hostname, simulator.GetMappedPublicPort(9000)).Uri,
                Timeout = TimeSpan.FromSeconds(10)
            };
            // Explicit synthetic camera; advertise mapped addresses so discovery works over NAT.
            // No discovery multicast, host networking, fixed host ports or personal configuration.
            using var created = await PostControl(control, "api/scenario", new
            {
                formatVersion = 1,
                config = new
                {
                    bindAddress = IPAddress.Any.ToString(),
                    controlApiAddress = IPAddress.Any.ToString(),
                    discoveryEnabled = false
                },
                cameras = new[]
                {
                    new
                    {
                        id = "synthetic-camera",
                        displayName = "Synthetic integration camera",
                        persona = "generic",
                        users = new[] { new { username, password, level = "Administrator" } },
                        network = new
                        {
                            bindAddress = IPAddress.Any.ToString(),
                            httpPort = 8000,
                            advertisedHost = simulator.Hostname,
                            advertisedHttpPort = simulator.GetMappedPublicPort(8000)
                        }
                    }
                }
            }, token);
            Assert.True(created.IsSuccessStatusCode, "Simulator scenario setup failed.");

            var endpoint = new UriBuilder("http", simulator.Hostname, simulator.GetMappedPublicPort(8000), "/onvif/device_service").Uri;
            var authentication = new OnvifAuthenticationSettings(DigestAuthentication.HttpDigest | DigestAuthentication.WsUsernameToken);
            using var client = new SimpleOnvifClient(endpoint.AbsoluteUri, username, password, authentication);
            await client.GetServicesAsync().WaitAsync(token);
            var subscription = await client.PullPointSubscribeAsync(120).WaitAsync(token);
            var address = subscription.SubscriptionReference?.Address?.Value;
            Assert.False(string.IsNullOrWhiteSpace(address));
            try
            {
                using var manager = new SubscriptionManagerClient(address!, new OnvifClientSettings
                {
                    Credentials = new NetworkCredential(username, password),
                    Authentication = new AddressedAuthentication(authentication, address!),
                    Timeout = TimeSpan.FromSeconds(10)
                });
                var renewed = await manager.RenewAsync(new RenewRequest { TerminationTime = "PT120S" }, token);
                Assert.True(renewed.TerminationTime > renewed.CurrentTime);

                // This unknown topic is deliberately synthetic. State is the simulator's
                // documented fallback field, not an assumption in application event handling.
                const string topic = "Synthetic/Scaffold/Detection";
                foreach (var state in new[] { true, false })
                {
                    using var triggered = await PostControl(control, "api/cameras/synthetic-camera/events", new { topic, state, duration = 0 }, token);
                    Assert.True(triggered.IsSuccessStatusCode, "Simulator event trigger failed.");
                    var found = false;
                    while (!found)
                    {
                        var batch = await client.PullPointPullMessagesAsync(address!, 1, 100).WaitAsync(token);
                        found = (batch.NotificationMessage ?? []).Any(n =>
                            string.Concat((n.Topic?.Any ?? []).Select(node => node.InnerText)) == topic &&
                            n.Message?.GetElementsByTagName("SimpleItem", "http://www.onvif.org/ver10/schema")
                                .OfType<XmlElement>().Any(item => item.GetAttribute("Name") == "State" &&
                                    item.GetAttribute("Value") == (state ? "true" : "false")) == true);
                    }
                }
            }
            finally
            {
                await client.PullPointUnsubscribeAsync(address!).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static async Task<HttpResponseMessage> PostControl<T>(HttpClient client, string path, T body, CancellationToken token)
        {
            using var content = JsonContent.Create(body);
            // This simulator revision expects Content-Length rather than chunked JSON.
            await content.LoadIntoBufferAsync(token);
            return await client.PostAsync(path, content, token);
        }
    }
}
