namespace OnvifWebhookBridge.Dispatch
{
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Onvif;
    using OnvifWebhookBridge.Rules;

    public static class PipelineServices
    {


        public static IServiceCollection AddCameraPipeline(this IServiceCollection services)
        {
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<EventHistory>();
            services.AddSingleton<EventNormalizer>();
            services.AddSingleton<RuleState>();
            services.AddSingleton<HttpActionDispatcher>();
            services.AddSingleton(provider => new CameraPipeline(provider.GetRequiredService<RuleState>(),
                provider.GetRequiredService<HttpActionDispatcher>().TrySubmit, provider.GetRequiredService<TimeProvider>()));
            services.AddHttpClient(HttpActionDispatcher.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(HttpActionDispatcher.CreateHandler)
                .RemoveAllLoggers();
            services.AddHttpClient(HttpActionDispatcher.UnverifiedClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(HttpActionDispatcher.CreateUnverifiedHandler)
                .RemoveAllLoggers();
            services.AddHostedService(provider => provider.GetRequiredService<HttpActionDispatcher>());
            services.AddHostedService<CameraSupervisor>();
            return services;
        }
    }
}
