namespace OnvifWebhookBridge.Events
{
    public sealed record EventField(string Kind, string? Name, string? Value, string Xml, string IdentityMetadata = "");
    public sealed record TopicAttribute(string Name, string Namespace, string Value);

    // Strings/collections are snapshots, never references to the mutable library XML tree.
    public sealed record NormalizedEvent(
        string CameraId, string CameraName, DateTimeOffset ReceivedAt,
        string Topic, string? Dialect, IReadOnlyList<TopicAttribute> TopicAttributes,
        IReadOnlyList<string> TopicXml, string? CameraTimeText, DateTimeOffset? CameraTime,
        string? PropertyOperation, IReadOnlyList<EventField> Source,
        IReadOnlyList<EventField> Data, IReadOnlyList<EventField> Key,
        string RawMessageXml, bool Truncated, IReadOnlyList<string> Diagnostics,
        int RetainedBytes);
}
