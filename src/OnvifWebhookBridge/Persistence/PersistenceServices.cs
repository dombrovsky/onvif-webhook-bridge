namespace OnvifWebhookBridge.Persistence
{
    using OnvifWebhookBridge.Dispatch;

    public static class PersistenceServices
    {
        public static IServiceCollection AddPersistentCameraPipeline(this IServiceCollection services, string dataDirectory)
        {
            services.AddSingleton(provider => new ConfigurationStore(dataDirectory, provider.GetRequiredService<CameraPipeline>()));
            // StartAsync completes restoration before the dispatcher/supervisor StartAsync calls.
            services.AddHostedService<ConfigurationStartup>();
            return services.AddCameraPipeline();
        }

        private sealed class ConfigurationStartup(ConfigurationStore store) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken) { store.Initialize(); return Task.CompletedTask; }
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
