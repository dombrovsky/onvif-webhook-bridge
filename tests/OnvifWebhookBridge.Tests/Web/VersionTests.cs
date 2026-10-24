namespace OnvifWebhookBridge.Tests.Web
{
    using OnvifWebhookBridge.Web;
    using System.Reflection;
    using System.Text.Json;
    using Xunit;

    public sealed class VersionTests
    {
        [Fact, Trait("Category", "Unit")]
        public void SessionUsesCompiledProductVersionWithoutSourceProvenance()
        {
            var assembly = typeof(SessionView).Assembly;
            var product = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            var numeric = assembly.GetName().Version!;
            var file = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
            Assert.Equal($"{numeric.Major}.{numeric.Minor}.{numeric.Build}.0", file);
            var session = new SessionView("synthetic-csrf", DateTimeOffset.UnixEpoch);
            Assert.Equal(product.Split('+')[0], session.Version);
            var json = JsonSerializer.Serialize(session, JsonContext.Default.SessionView);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(session.Version, document.RootElement.GetProperty("version").GetString());
        }
    }
}
