namespace OnvifWebhookBridge.Tests.Web
{
    using System.Net;
    using static OnvifWebhookBridge.Tests.Persistence.StoragePath;
    using static SecurityPath;

    internal static class BrowserPath
    {
        public static async Task Verify(HttpClient client, CancellationToken token)
        {
            foreach (var path in new[] { "/", "/app.js", "/app.css" })
            {
                using var response = await client.GetAsync(path, token);
                Check(response.IsSuccessStatusCode, "BrowserAssetAvailable");
                Check(response.Headers.CacheControl?.NoStore == true, "BrowserNoStore");
                var csp = response.Headers.GetValues("Content-Security-Policy").Single();
                Check(csp.Contains("script-src 'self'", StringComparison.Ordinal) && !csp.Contains("unsafe-inline", StringComparison.Ordinal) && csp.Contains("base-uri 'none'", StringComparison.Ordinal), "BrowserCsp");
                var text = await response.Content.ReadAsStringAsync(token);
                Check(!text.Contains("synthetic-admin-password", StringComparison.Ordinal), "ShellHasNoCredentials");
                Check(!text.Contains("innerHTML", StringComparison.Ordinal) && !text.Contains("eval(", StringComparison.Ordinal), "NoActiveMarkupSink");
            }
            using var denied = await client.GetAsync("/wwwroot/index.html", token);
            Check(denied.StatusCode == HttpStatusCode.Unauthorized, "NoStaticDirectoryBypass");
        }
    }
}
