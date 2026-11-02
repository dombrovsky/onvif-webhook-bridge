namespace OnvifWebhookBridge.Dispatch
{
    public enum ExecutionResult
    {
        Queued, Running, Accepted, HttpFailure, TransportFailure, Timeout, Cancelled, Stale, Expired, Overflow
    }
    public sealed record ExecutionStatus(Guid JobId, string RuleId, DateTimeOffset Time, ExecutionResult Result,
        int Attempts = 0, int? StatusCode = null, TimeSpan Duration = default,
        string? ResponseJson = null, string? ResponseBodyStatus = null, string? TransportError = null);

    public sealed record DispatchJob(Guid Id, string RuleId, long RuleRevision, long CameraRevision,
        HttpAction Action, bool Manual, long EnqueuedAt, CancellationToken Cancellation)
    {
        // A result on the existing job, not a second lifecycle registry.
        internal ExecutionResult MetricResult = ExecutionResult.TransportFailure;
        private int metricState;
        private TimeSpan metricDuration;

        internal void MetricAccepted()
        {
            // Completion may run before synchronous TrySubmit returns. The second
            // participant records once; rejected operations never set the accepted bit.
            if (Interlocked.Or(ref metricState, 1) == 2)
            {
                BridgeMetrics.Complete(MetricResult, metricDuration);
            }
        }

        internal void MetricCompleted(TimeSpan duration)
        {
            metricDuration = duration;
            if (Interlocked.Or(ref metricState, 2) == 1)
            {
                BridgeMetrics.Complete(MetricResult, metricDuration);
            }
        }
    }
}
