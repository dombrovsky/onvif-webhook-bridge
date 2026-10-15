using OnvifWebhookBridge.Dispatch;
using OnvifWebhookBridge.Persistence;
using OnvifWebhookBridge.Web;

if (args is ["--health-probe"])
{
    Environment.ExitCode = await HealthProbe.Run(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();
// Request diagnostics can contain configured addresses. Add safe logging per feature.
builder.Logging.ClearProviders();
builder.Services.AddPersistentCameraPipeline(Environment.GetEnvironmentVariable("ONVIF_DATA_DIRECTORY") ??
    (OperatingSystem.IsLinux() ? "/data" : Path.Combine(AppContext.BaseDirectory, "data")));
builder.Services.AddAdministration();
var app = builder.Build();
try
{
    app.Services.GetRequiredService<ConfigurationStore>().Initialize();
    app.Services.GetRequiredService<AdminAuthentication>().Bootstrap(Environment.GetEnvironmentVariable,
        args.Contains("--reset-administrator", StringComparer.Ordinal));
    if (args.Contains("--reset-administrator", StringComparer.Ordinal))
    {
        return;
    }
    if (!app.Services.GetRequiredService<ConfigurationStore>().Read().HasAdministrator)
    {
        throw new InvalidOperationException("Administrator setup is required.");
    }
}
catch (Exception)
{
    Console.Error.WriteLine("Configuration or administrator initialization failed. Check storage and administrator settings.");
    Environment.ExitCode = 1;
    return;
}
app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<CameraPipeline>()
    .SetSelfOrigins(app.Urls.Select(url => Uri.TryCreate(url, UriKind.Absolute, out var origin) ? origin : null)
        .OfType<Uri>()));
app.MapAdministration();
app.Run();
