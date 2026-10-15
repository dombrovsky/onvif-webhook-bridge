namespace OnvifWebhookBridge.Tests.Persistence
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Persistence;
    using Xunit;

    public sealed class StorageTests
    {
        [Fact]
        public void StorageAcceptanceRunsOnJitAndNative() => StoragePath.Verify();

        [Fact]
        public async Task SimultaneousEditsHaveOneWinner()
        {
            using var temp = new TemporaryStorage();
            var first = new ConfigurationStore(temp.Path, StoragePath.Pipeline());
            var second = new ConfigurationStore(temp.Path, StoragePath.Pipeline());
            first.Initialize();
            second.Initialize();
            using var ready = new Barrier(2);
            async Task<bool> Edit(ConfigurationStore store) => await Task.Run(() =>
            {
                ready.SignalAndWait();
                try
                {
                    store.Save(0, [StoragePath.Camera()], []);
                    return true;
                }
                catch (RevisionConflictException) { return false; }
            });
            var result = await Task.WhenAll(Edit(first), Edit(second));
            Assert.Single(result, x => x);
            Assert.Equal(1, first.Read().Revision);
        }

        [Fact]
        public async Task CorruptStartupFailsBeforeAnyActiveConfiguration()
        {
            using var temp = new TemporaryStorage();
            Directory.CreateDirectory(temp.Path);
            File.WriteAllText(temp.Database, "synthetic-corrupt-bootstrap-secret");
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddPersistentCameraPipeline(temp.Path);
            using var host = builder.Build();
            var error = await Assert.ThrowsAsync<StorageException>(() => host.StartAsync());
            Assert.DoesNotContain("synthetic-corrupt-bootstrap-secret", error.ToString());
            Assert.Empty(host.Services.GetRequiredService<CameraPipeline>().Cameras);
            Assert.Equal("synthetic-corrupt-bootstrap-secret", File.ReadAllText(temp.Database));
        }
    }
}
