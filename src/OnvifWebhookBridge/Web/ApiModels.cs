namespace OnvifWebhookBridge.Web
{
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Rules;

    public sealed record LoginRequest(string Username, string Password);
    public sealed record SessionView(string CsrfToken, DateTimeOffset Expires);
    public sealed record ApiError(string Code, string Message);
    public sealed record RevisionView(long Revision);
    public sealed record RevisionRequest(long Revision);
    public sealed record EnabledRequest(long Revision, bool Enabled);
    public sealed record StringSecret(SecretOperation Operation = SecretOperation.Preserve, string? Value = null)
    {
        internal SecretEdit<string> Edit() => new(Operation, Value);
    }
    public sealed record HeaderSecret(SecretOperation Operation = SecretOperation.Preserve, ActionHeader[]? Value = null)
    {
        internal SecretEdit<ActionHeader[]> Edit() => new(Operation, Value);
    }
    public sealed record CameraRequest(long Revision, string Name, string Endpoint, bool Enabled = false,
        StringSecret? Username = null, StringSecret? Password = null)
    {
        internal CameraEdit Edit(string id) => new(id, Name, Endpoint, Enabled, Username?.Edit(), Password?.Edit());
    }
    public sealed record RuleRequest(long Revision, string Name, string CameraId, string Topic, FieldCondition? Condition = null,
        bool Enabled = false, OccurrenceMode Mode = OccurrenceMode.Occurrence, double CooldownSeconds = 0,
        string Method = "GET", int AdditionalAttempts = 0, StringSecret? Url = null, HeaderSecret? Headers = null, StringSecret? JsonBody = null)
    {
        internal RuleEdit Edit(string id)
        {
            if (!double.IsFinite(CooldownSeconds) || CooldownSeconds < 0 || CooldownSeconds > TimeSpan.MaxValue.TotalSeconds)
            {
                throw new ArgumentException();
            }
            return new(new(id, CameraId, Topic, Condition, Enabled, Mode, TimeSpan.FromSeconds(CooldownSeconds)), Name,
                Method, AdditionalAttempts, Url?.Edit(), Headers?.Edit(), JsonBody?.Edit());
        }
    }
    public sealed record ConnectivityView(bool Reachable, string Result);
}
