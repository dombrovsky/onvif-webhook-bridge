namespace OnvifWebhookBridge.Dispatch
{
    using System.Net;
    using System.Text;
    using System.Text.Json;

    public sealed record ActionHeader(string Name, string Value);

    // Validated, detached configuration; never include this object in diagnostics.
    public sealed class HttpAction : IEquatable<HttpAction>
    {
        public Uri Address { get; }
        public string Method { get; }
        public IReadOnlyList<ActionHeader> Headers { get; }
        public string? JsonBody { get; }
        public int AdditionalAttempts { get; }

        public bool Equals(HttpAction? other) => other is not null && Address == other.Address && Method == other.Method &&
            JsonBody == other.JsonBody && AdditionalAttempts == other.AdditionalAttempts && Headers.SequenceEqual(other.Headers);
        public override bool Equals(object? obj) => obj is HttpAction other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Address, Method, JsonBody, AdditionalAttempts);

        public HttpAction(string url, string method = "GET", IEnumerable<ActionHeader>? headers = null,
            string? jsonBody = null, int additionalAttempts = 0)
        {
            if (url.Length > 8192 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var address) ||
                address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0 || address.Fragment.Length != 0 ||
                ForbiddenHost(address.Host) || method is not ("GET" or "POST") || additionalAttempts is < 0 or > 2)
            { throw new ArgumentException("Invalid HTTP action."); }
            var copy = (headers ?? []).Take(33).ToArray();
            if (copy.Length > 32 || copy.Sum(h => (long)Encoding.UTF8.GetByteCount(h.Name) + Encoding.UTF8.GetByteCount(h.Value)) > 16384 ||
                copy.Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
            { throw new ArgumentException("Invalid HTTP headers."); }
            foreach (var h in copy)
            {
                if (h.Name.Length == 0 || h.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) ||
                    h.Value.Any(char.IsControl) || h.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
                    new[] { "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Expect", "Trailer", "TE", "Proxy-Authorization", "Proxy-Connection" }
                        .Contains(h.Name, StringComparer.OrdinalIgnoreCase))
                { throw new ArgumentException("Invalid HTTP headers."); }
            }
            try
            {
                using var validation = new HttpRequestMessage();
                foreach (var header in copy) { validation.Headers.Add(header.Name, header.Value); }
            }
            catch (Exception error) when (error is FormatException or InvalidOperationException)
            { throw new ArgumentException("Invalid HTTP headers."); }
            if (jsonBody is not null)
            {
                if (method != "POST" || Encoding.UTF8.GetByteCount(jsonBody) > 65536) { throw new ArgumentException("Invalid JSON body."); }
                try { using var document = JsonDocument.Parse(jsonBody, new JsonDocumentOptions { MaxDepth = 64 }); }
                catch (JsonException) { throw new ArgumentException("Invalid JSON body."); }
            }
            Address = address; Method = method; Headers = Array.AsReadOnly(copy); JsonBody = jsonBody; AdditionalAttempts = additionalAttempts;
        }

        internal static bool ForbiddenHost(string host) => host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(host.Trim('[', ']'), out var ip) && ForbiddenAddress(ip);

        internal static bool ForbiddenAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) { address = address.MapToIPv4(); }
            var bytes = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
                (bytes.Length == 16 && bytes.AsSpan().SequenceEqual(new byte[] { 0xfd, 0, 0x0e, 0xc2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0x54 })) ||
                (bytes.Length == 4 && (bytes[0] == 169 && bytes[1] == 254 || bytes[0] >= 224 || bytes[0] == 0));
        }
    }
}
