namespace OnvifWebhookBridge.Tests.Onvif
{
    using OnvifWebhookBridge.Onvif;
    using SharpOnvifClient.Events;
    using SharpOnvifCommon.Security;
    using SharpOnvifCommon.Soap;
    using System.Net;
    using System.Xml.Linq;
    using Xunit;

    public sealed class AddressedAuthenticationTests
    {
        [Fact]
        [Trait("Category", "Unit")]
        public async Task RenewWritesAddressingWithUniqueMessageIdsAndRetainsSecurity()
        {
            const string address = "https://subscription.example.invalid/pull";
            using var transport = new RenewalRecorder();
            using var manager = new SubscriptionManagerClient(address, new OnvifClientSettings
            {
                Transport = transport,
                Credentials = new NetworkCredential("synthetic-user", "synthetic-password"),
                Authentication = new AddressedAuthentication(
                    new OnvifAuthenticationSettings(DigestAuthentication.WsUsernameToken), address)
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await manager.RenewAsync(new RenewRequest { TerminationTime = "PT120S" }, deadline.Token);
            await manager.RenewAsync(new RenewRequest { TerminationTime = "PT120S" }, deadline.Token);

            XNamespace soap = "http://www.w3.org/2003/05/soap-envelope";
            XNamespace addressing = "http://www.w3.org/2005/08/addressing";
            XNamespace security = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
            var ids = new HashSet<string>();
            Assert.Equal(2, transport.Requests.Count);
            foreach (var request in transport.Requests)
            {
                var header = request.Root!.Element(soap + "Header")!;
                var id = Assert.Single(header.Elements(addressing + "MessageID")).Value;
                Assert.StartsWith("urn:uuid:", id);
                Assert.True(ids.Add(id));
                Assert.EndsWith("RenewRequest", Assert.Single(header.Elements(addressing + "Action")).Value);
                Assert.Equal(address, Assert.Single(header.Elements(addressing + "To")).Value);
                Assert.Equal(addressing.NamespaceName + "/anonymous", header.Element(addressing + "ReplyTo")?.Element(addressing + "Address")?.Value);
                Assert.NotNull(header.Element(security + "Security")?.Element(security + "UsernameToken"));
            }
        }

        // Synthetic transport at unit scope only; simulator integration is separate.
        private sealed class RenewalRecorder : HttpMessageHandler
        {
            internal List<XDocument> Requests { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:n="http://docs.oasis-open.org/wsn/b-2">
                          <s:Body><n:RenewResponse><n:TerminationTime>2030-01-01T00:02:00Z</n:TerminationTime><n:CurrentTime>2030-01-01T00:00:00Z</n:CurrentTime></n:RenewResponse></s:Body>
                        </s:Envelope>
                        """, System.Text.Encoding.UTF8, "application/soap+xml")
                };
            }
        }
    }
}
