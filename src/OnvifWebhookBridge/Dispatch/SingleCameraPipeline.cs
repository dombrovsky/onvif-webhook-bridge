namespace OnvifWebhookBridge.Dispatch
{
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Rules;

    public sealed record CameraConfiguration(string Id, string Name, string Endpoint, string? Username = null,
        string? Password = null, bool Enabled = true);
    public sealed record ActionRule(ForwardingRule Match, HttpAction Action);

    // One lock orders configuration, matching, admission and execution eligibility checks.
    // No administration endpoints; callers supply private in-memory configuration through DI.
    public sealed class SingleCameraPipeline(RuleState rules, DispatchQueue queue, TimeProvider clock)
    {
        private readonly object gate = new();
        private CameraConfiguration? camera;
        private long cameraRevision;
        private long revision;
        private Dictionary<string, (ActionRule Rule, long Revision)> configured = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionStatus> status = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionStatus> executions = new(StringComparer.Ordinal);
        private Uri[] selfOrigins = [];

        public CameraConfiguration? Camera { get { lock (gate) { return camera; } } }
        public IReadOnlyList<ExecutionStatus> Status { get { lock (gate) { return status.Values.ToArray(); } } }
        public IReadOnlyList<ExecutionStatus> LastExecutions { get { lock (gate) { return executions.Values.ToArray(); } } }

        public void SetSelfOrigins(IEnumerable<Uri> origins)
        { lock (gate) { selfOrigins = origins.Take(64).ToArray(); } }
        public bool IsCurrentCamera(CameraConfiguration snapshot) { lock (gate) { return camera == snapshot; } }

        public void Configure(CameraConfiguration? source, IEnumerable<ActionRule> actions)
        {
            var copy = actions.Take(RuleState.MaximumRules + 1).ToArray();
            if (source is not null && (string.IsNullOrEmpty(source.Id) || source.Id.Length > 4096 || source.Name.Length > 4096 ||
                !Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
                endpoint.UserInfo.Length != 0 || source.Endpoint.Length > 8192 || source.Endpoint.Any(char.IsControl) ||
                (source.Username?.Length ?? 0) > 4096 || (source.Password?.Length ?? 0) > 4096 ||
                string.IsNullOrEmpty(source.Username) != string.IsNullOrEmpty(source.Password)))
            { throw new ArgumentException("Invalid camera configuration."); }
            if (copy.Any(r => r.Match.CameraId != source?.Id)) { throw new ArgumentException("Unknown camera."); }
            lock (gate)
            {
                rules.SetRules(copy.Select(r => r.Match));
                var replacement = new Dictionary<string, (ActionRule, long)>(StringComparer.Ordinal);
                foreach (var rule in copy)
                {
                    replacement.Add(rule.Match.Id, configured.TryGetValue(rule.Match.Id, out var old) && old.Rule == rule
                        ? old : (rule, ++revision));
                }
                // Action-only edits reset only that rule's cooldown/transition state.
                foreach (var r in copy.Where(r => configured.TryGetValue(r.Match.Id, out var old) && old.Rule != r))
                { rules.ResetRule(r.Match.Id); }
                if (camera != source) { cameraRevision++; if (source is not null) { rules.ResetCamera(source.Id); } }
                camera = source; configured = replacement;
                foreach (var id in status.Keys.Where(id => !configured.ContainsKey(id)).ToArray()) { status.Remove(id); }
                foreach (var id in executions.Keys.Where(id => !configured.ContainsKey(id)).ToArray()) { executions.Remove(id); }
            }
        }

        public IReadOnlyList<RuleDecision> Process(NormalizedEvent notification, CameraConfiguration? sourceSnapshot = null)
        {
            lock (gate)
            { return rules.Process(notification, (r, _) => Admit(r.Id, false, default),
                camera is { Enabled: true } && (sourceSnapshot is null || camera == sourceSnapshot)); }
        }
        public void ResetEpoch() { lock (gate) { if (camera is not null) { rules.ResetCamera(camera.Id); } } }
        public bool ManualTest(string ruleId, CancellationToken cancellation = default)
        { lock (gate) { return Admit(ruleId, true, cancellation); } }
        private bool Admit(string id, bool manual, CancellationToken cancellation)
        {
            if (camera is not { Enabled: true } || !configured.TryGetValue(id, out var entry) || !manual && !entry.Rule.Match.Enabled)
            { return false; }
            var job = new DispatchJob(Guid.NewGuid(), id, entry.Revision, cameraRevision, entry.Rule.Action, manual, clock.GetTimestamp(), cancellation);
            var accepted = queue.TryEnqueue(job);
            status[id] = new(job.Id, id, clock.GetUtcNow(), accepted ? ExecutionResult.Queued : ExecutionResult.Overflow);
            return accepted;
        }
        public bool Eligible(DispatchJob job)
        {
            lock (gate)
            { return camera is { Enabled: true } && cameraRevision == job.CameraRevision &&
                configured.TryGetValue(job.RuleId, out var entry) && entry.Revision == job.RuleRevision && (job.Manual || entry.Rule.Match.Enabled) &&
                !selfOrigins.Any(origin => origin.Port == job.Action.Address.Port &&
                    (origin.Host == job.Action.Address.Host || origin.IsLoopback && job.Action.Address.IsLoopback ||
                        System.Net.IPAddress.TryParse(origin.Host.Trim('[', ']'), out var bind) &&
                        (bind.Equals(System.Net.IPAddress.Any) || bind.Equals(System.Net.IPAddress.IPv6Any)) && job.Action.Address.IsLoopback)); }
        }
        public void Record(ExecutionStatus result)
        {
            lock (gate)
            {
                // An older active job must not overwrite a newer admission's status.
                if (status.TryGetValue(result.RuleId, out var current) && current.JobId == result.JobId) { status[result.RuleId] = result; }
                if (configured.ContainsKey(result.RuleId) && result.Result is not (ExecutionResult.Running or ExecutionResult.Queued))
                { executions[result.RuleId] = result; }
            }
        }
    }
}
