namespace OnvifWebhookBridge.Web
{
    // Explicit embedded allowlist works independently of the host's working directory under AOT.
    internal static class BrowserAssets
    {
        internal static bool IsAsset(PathString path) => path == "/" || path == "/app.css" || path == "/app.js";

        internal static async Task Write(HttpContext context)
        {
            var file = context.Request.Path == "/" ? "index.html" : context.Request.Path.Value![1..];
            context.Response.ContentType = file switch
            {
                "index.html" => "text/html; charset=utf-8",
                "app.css" => "text/css; charset=utf-8",
                _ => "text/javascript; charset=utf-8"
            };
            using var stream = typeof(BrowserAssets).Assembly.GetManifestResourceStream("OnvifWebhookBridge.wwwroot." + file)!;
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }
}
