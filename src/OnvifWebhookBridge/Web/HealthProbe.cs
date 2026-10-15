namespace OnvifWebhookBridge.Web
{
    using System.Net;

    internal static class HealthProbe
    {
        public static async Task<int> Run(string? ports)
        {
            if (!int.TryParse(ports ?? "8080", out var port) || port is < 1 or > 65535)
            {
                return 1;
            }
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(4),
                    MaxResponseContentBufferSize = 7
                };
                using var response = await client.GetAsync(new UriBuilder("http", IPAddress.Loopback.ToString(), port, "/health").Uri);
                return response.StatusCode == HttpStatusCode.OK &&
                    (await response.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual("Healthy"u8) ? 0 : 1;
            }
            catch (Exception)
            {
                return 1;
            }
        }
    }
}
