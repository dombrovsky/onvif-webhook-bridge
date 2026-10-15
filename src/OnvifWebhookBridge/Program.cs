using OnvifWebhookBridge.Dispatch;
using OnvifWebhookBridge.Persistence;

var builder = WebApplication.CreateSlimBuilder(args);
// Request diagnostics can contain configured addresses. Add safe logging per feature.
builder.Logging.ClearProviders();
builder.Services.AddPersistentCameraPipeline(Environment.GetEnvironmentVariable("ONVIF_DATA_DIRECTORY") ??
    (OperatingSystem.IsLinux() ? "/data" : Path.Combine(AppContext.BaseDirectory, "data")));
var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<CameraPipeline>()
    .SetSelfOrigins(app.Urls.Select(url => Uri.TryCreate(url, UriKind.Absolute, out var origin) ? origin : null)
        .OfType<Uri>()));
app.MapGet("/health", () => Results.Text("Healthy"));
app.Run();
