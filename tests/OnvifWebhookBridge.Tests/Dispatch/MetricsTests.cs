namespace OnvifWebhookBridge.Tests.Dispatch
{
    using Xunit;

    [CollectionDefinition("Metrics", DisableParallelization = true)]
    public sealed class MetricsCollection;

    [Collection("Metrics"), Trait("Category", "Unit")]
    public sealed class MetricsTests
    {
        [Fact]
        public Task ExportsCumulativeRedactedMetricsAndSettlesEveryAcceptedJob() => MetricsPath.Verify();
    }
}
