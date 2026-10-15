// Scaffold only: administration and camera workers are added in later phases.
var builder = WebApplication.CreateSlimBuilder(args);
// Request diagnostics can contain configured addresses. Add safe logging per feature.
builder.Logging.ClearProviders();
var app = builder.Build();
app.MapGet("/health", () => Results.Text("Healthy"));
app.Run();
