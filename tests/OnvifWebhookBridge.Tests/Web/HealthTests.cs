namespace OnvifWebhookBridge.Tests.Web
{
    using Microsoft.Extensions.DependencyInjection;
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Tests.Persistence;
    using OnvifWebhookBridge.Web;
    using System.Net;
    using Xunit;

    public sealed class HealthTests
    {
        [Fact, Trait("Category", "Unit")]
        public async Task ProbeUsesConfiguredPortAndFailsClosedWithoutStorage()
        {
            using var data = new TemporaryStorage();
            await using var app = await SecurityPath.Host(data.Path, CancellationToken.None);
            using var client = SecurityPath.Client(app);
            var store = app.Services.GetRequiredService<ConfigurationStore>();
            store.BootstrapAdministrator("synthetic-admin", "synthetic-admin-password");
            var port = client.BaseAddress!.Port.ToString();
            Assert.Equal(0, await HealthProbe.Run(port));
            Assert.Equal(1, await HealthProbe.Run("8080;8081"));
            Assert.Equal(1, await HealthProbe.Run("0"));
            // A failed persistent read must report a fixed failure, with no secret details.
            File.Move(Path.Combine(data.Path, "configuration.db"), Path.Combine(data.Path, "saved.db"));
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync());
            Assert.Equal(1, await HealthProbe.Run(port));
        }
    }
}
