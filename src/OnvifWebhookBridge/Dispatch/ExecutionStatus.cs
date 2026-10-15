namespace OnvifWebhookBridge.Dispatch
{
    public enum ExecutionResult
    {
        Queued, Running, Accepted, HttpFailure, TransportFailure, Timeout, Cancelled, Stale, Expired, Overflow
    }
    public sealed record ExecutionStatus(Guid JobId, string RuleId, DateTimeOffset Time, ExecutionResult Result,
        int Attempts = 0, int? StatusCode = null, TimeSpan Duration = default);

    public sealed record DispatchJob(Guid Id, string RuleId, long RuleRevision, long CameraRevision,
        HttpAction Action, bool Manual, long EnqueuedAt, CancellationToken Cancellation);
}
