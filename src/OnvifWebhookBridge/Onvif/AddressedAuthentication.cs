namespace OnvifWebhookBridge.Onvif
{
    using SharpOnvifCommon.Onvif;
    using SharpOnvifCommon.Security;
    using SharpOnvifCommon.Soap;
    using SharpOnvifCommon.Xml;

    // 0.11.2 writes authentication headers but no WS-Addressing headers on Renew.
    // Some subscription managers require MessageID even when authentication is disabled.
    // Use the supported header hook, preserving both SOAP and HTTP authentication.
    internal sealed class AddressedAuthentication(OnvifAuthenticationSettings inner, string address) : IClientAuthentication
    {
        public bool RequiresOwnTransport(IClientSettings settings) => inner.RequiresOwnTransport(settings);

        public HttpMessageHandler CreateTransport(HttpMessageHandler transport, IClientSettings settings)
            => inner.CreateTransport(transport, settings);

        public Action<IXmlWriter> CreateSecurityHeader(string action, IClientSettings settings)
        {
            var securityHeader = inner.CreateSecurityHeader(action, settings);
            return writer =>
            {
                const string ns = "http://www.w3.org/2005/08/addressing";
                writer.WriteElementString(ns, "Action", action);
                writer.WriteElementString(ns, "MessageID", $"urn:uuid:{Guid.NewGuid()}");
                writer.WriteElementString(ns, "To", address);
                writer.WriteStartElement(ns, "ReplyTo");
                writer.WriteElementString(ns, "Address", ns + "/anonymous");
                writer.WriteEndElement();
                securityHeader?.Invoke(writer);
            };
        }
    }
}
