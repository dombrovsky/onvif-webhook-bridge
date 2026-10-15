namespace OnvifWebhookBridge.Dispatch
{
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Onvif;
    using OnvifWebhookBridge.Rules;

    public static class PipelineServices
    {
        public static IServiceCollection AddSingleCameraPipeline(this IServiceCollection services)
        {
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<EventHistory>();
            services.AddSingleton<EventNormalizer>();
            services.AddSingleton<RuleState>();
            services.AddSingleton<DispatchQueue>();
            services.AddSingleton<SingleCameraPipeline>();
            services.AddHttpClient(HttpActionDispatcher.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(HttpActionDispatcher.CreateHandler)
                .RemoveAllLoggers();
            services.AddHostedService<HttpActionDispatcher>();
            services.AddHostedService<SingleCameraWorker>();
            return services;
        }
    }
}
