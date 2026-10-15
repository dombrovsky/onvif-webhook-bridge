namespace OnvifWebhookBridge.Rules
{
    public enum FieldSection
    {
        Source, Data
    }
    public enum ComparisonMode
    {
        Text, Boolean
    }
    public enum OccurrenceMode
    {
        Occurrence, Transition
    }
    public sealed record FieldCondition(FieldSection Section, string Name, string ExpectedValue,
        ComparisonMode Comparison = ComparisonMode.Text);

    // Matching configuration stays independent of the HTTP action snapshot.
    public sealed record ForwardingRule(string Id, string CameraId, string Topic,
        FieldCondition? Condition = null, bool Enabled = false,
        OccurrenceMode Mode = OccurrenceMode.Occurrence, TimeSpan Cooldown = default);
    public enum DecisionKind
    {
        Disabled, Unmatched, Baseline, Deleted, Unchanged, Cooldown, Rejected, Accepted, UnsafeContent
    }
    public sealed record RuleDecision(string RuleId, DecisionKind Kind);
}
