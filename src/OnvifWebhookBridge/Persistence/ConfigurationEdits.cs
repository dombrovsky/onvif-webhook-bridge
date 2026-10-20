namespace OnvifWebhookBridge.Persistence
{
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Rules;

    public sealed class StorageException(string category) : Exception("Configuration storage: " + category);
    public sealed class RevisionConflictException() : Exception("Configuration revision conflict.");

    public enum SecretOperation
    {
        Preserve, Replace, Clear
    }
    // Never echo edit objects or their values into diagnostics. Masked views contain presence flags only.
    public sealed class SecretEdit<T>(SecretOperation operation = SecretOperation.Preserve, T? value = default) where T : class
    {
        public SecretOperation Operation { get; } = operation;
        internal T? Value { get; } = value;
        internal T? Resolve(T? previous) => Operation switch
        {
            SecretOperation.Preserve when Value is null => previous,
            SecretOperation.Replace when Value is not null => Value,
            SecretOperation.Clear when Value is null => default,
            _ => throw new ArgumentException("Invalid secret edit.")
        };
    }
    public sealed class CameraEdit(string id, string name, string endpoint, bool enabled = true,
        SecretEdit<string>? username = null, SecretEdit<string>? password = null)
    {
        internal CameraConfiguration Resolve(CameraConfiguration? old) => new(id, name, endpoint,
            (username ?? new()).Resolve(old?.Username), (password ?? new()).Resolve(old?.Password), enabled);
        internal string Id => id;
    }
    public sealed class RuleEdit(ForwardingRule match, string name, string method = "GET", int additionalAttempts = 0,
        SecretEdit<string>? url = null, SecretEdit<ActionHeader[]>? headers = null, SecretEdit<string>? jsonBody = null,
        bool ignoreCertificateValidation = false, bool? urlSensitive = null)
    {
        internal string Id => match.Id;
        internal StoredRule Resolve(StoredRule? old)
        {
            var sensitive = urlSensitive ?? old?.UrlSensitive ?? true;
            if (old is { UrlSensitive: true } && !sensitive && url?.Operation != SecretOperation.Replace)
            {
                throw new ArgumentException("Re-enter the URL before making it public.");
            }
            return new(name, new(match, new HttpAction(
            (url ?? new()).Resolve(old?.Rule.Action.Address.AbsoluteUri) ?? "", method,
            (headers ?? new()).Resolve(old?.Rule.Action.Headers.ToArray()),
            (jsonBody ?? new()).Resolve(old?.Rule.Action.JsonBody), additionalAttempts, ignoreCertificateValidation)), sensitive);
        }
    }
    public sealed record CameraView(string Id, string Name, string Endpoint, bool Enabled, bool HasUsername, bool HasPassword);
    public sealed record RuleView(ForwardingRule Match, string Name, string Method, int AdditionalAttempts,
        bool HasUrl, bool HasHeaders, bool HasJsonBody, bool IgnoreCertificateValidation = false,
        bool UrlSensitive = true, string? PublicUrl = null);
    public sealed record ConfigurationView(long Revision, IReadOnlyList<CameraView> Cameras, IReadOnlyList<RuleView> Rules,
        bool HasAdministrator);
    internal sealed record StoredRule(string Name, ActionRule Rule, bool UrlSensitive = true);
    internal sealed record StoredConfiguration(long Revision, CameraConfiguration[] Cameras, StoredRule[] Rules, bool HasAdministrator);
}
