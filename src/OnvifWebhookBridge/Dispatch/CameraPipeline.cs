namespace OnvifWebhookBridge.Dispatch
{
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Rules;
    using System.Threading.Channels;

    public sealed record CameraConfiguration(string Id, string Name, string Endpoint, string? Username = null,
        string? Password = null, bool Enabled = true);
    public sealed record ActionRule(ForwardingRule Match, HttpAction Action);

    // One lock orders configuration, matching, admission and execution eligibility checks.
    // No administration endpoints; callers supply private in-memory configuration through DI.
    public sealed class CameraPipeline(RuleState rules, HttpActionDispatcher? dispatcher, TimeProvider clock)
    {
        private readonly object gate = new();
        private Dictionary<string, (CameraConfiguration Camera, long Revision)> cameras = new(StringComparer.Ordinal);
        private readonly Channel<bool> changes = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private long revision;
        private Dictionary<string, (ActionRule Rule, long Revision)> configured = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionStatus> status = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionStatus> executions = new(StringComparer.Ordinal);
        private Uri[] selfOrigins = [];

        public IReadOnlyList<CameraConfiguration> Cameras
        {
            get
            {
                lock (gate)
                {
                    return cameras.Values.Select(c => c.Camera).ToArray();
                }
            }
        }
        internal ValueTask<bool> WaitForChange(CancellationToken token) => changes.Reader.ReadAsync(token);
        public IReadOnlyList<ExecutionStatus> Status
        {
            get
            {
                lock (gate)
                {
                    return status.Values.ToArray();
                }
            }
        }
        public IReadOnlyList<ExecutionStatus> LastExecutions
        {
            get
            {
                lock (gate)
                {
                    return executions.Values.ToArray();
                }
            }
        }

        public void SetSelfOrigins(IEnumerable<Uri> origins)
        {
            lock (gate)
            {
                selfOrigins = origins.Take(64).ToArray();
            }
        }
        public bool IsSelfAddress(Uri address)
        {
            lock (gate)
            {
                return selfOrigins.Any(origin => origin.Port == address.Port &&
                    (origin.Host == address.Host || origin.IsLoopback && address.IsLoopback ||
                        System.Net.IPAddress.TryParse(origin.Host.Trim('[', ']'), out var bind) &&
                        (bind.Equals(System.Net.IPAddress.Any) || bind.Equals(System.Net.IPAddress.IPv6Any)) && address.IsLoopback));
            }
        }
        public bool IsCurrentCamera(CameraConfiguration snapshot)
        {
            lock (gate)
            {
                return cameras.TryGetValue(snapshot.Id, out var entry) && ReferenceEquals(entry.Camera, snapshot);
            }
        }

        public void Configure(CameraConfiguration? source, IEnumerable<ActionRule> actions)
            => ConfigureCameras(source is null ? [] : [source], actions);

        public void ConfigureCameras(IEnumerable<CameraConfiguration> sources, IEnumerable<ActionRule> actions)
        {
            var cameraCopy = sources.Take(65).ToArray();
            if (cameraCopy.Length > 64 || cameraCopy.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cameraCopy.Length)
            {
                throw new ArgumentException("Camera count or duplicate ID is invalid.");
            }
            var copy = actions.Take(RuleState.MaximumRules + 1).ToArray();
            foreach (var source in cameraCopy)
            {
                if (string.IsNullOrEmpty(source.Id) || source.Id.Length > 4096 || source.Name.Length > 4096 ||
                    !Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
                    endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 || HttpAction.ForbiddenHost(endpoint.Host) ||
                    source.Endpoint.Length > 8192 || source.Endpoint.Any(char.IsControl) ||
                    (source.Username?.Length ?? 0) > 4096 || (source.Password?.Length ?? 0) > 4096 ||
                    string.IsNullOrEmpty(source.Username) != string.IsNullOrEmpty(source.Password))
                {
                    throw new ArgumentException("Invalid camera configuration.");
                }
            }
            if (copy.Any(r => !cameraCopy.Any(c => c.Id == r.Match.CameraId)))
            {
                throw new ArgumentException("Unknown camera.");
            }
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
                {
                    rules.ResetRule(r.Match.Id);
                }
                var next = new Dictionary<string, (CameraConfiguration, long)>(StringComparer.Ordinal);
                foreach (var source in cameraCopy)
                {
                    if (cameras.TryGetValue(source.Id, out var old) && old.Camera == source)
                    {
                        next.Add(source.Id, old);
                    }
                    else
                    {
                        // Clone changed snapshots so removing/recreating equal configuration cannot revive an old pull.
                        next.Add(source.Id, (source with
                        {
                        }, ++revision));
                        rules.ResetCamera(source.Id);
                    }
                }
                cameras = next;
                configured = replacement;
                foreach (var id in status.Keys.Where(id => !configured.ContainsKey(id)).ToArray())
                {
                    status.Remove(id);
                }
                foreach (var id in executions.Keys.Where(id => !configured.ContainsKey(id)).ToArray())
                {
                    executions.Remove(id);
                }
                changes.Writer.TryWrite(true);
            }
        }

        public IReadOnlyList<RuleDecision> Process(NormalizedEvent notification, CameraConfiguration? sourceSnapshot = null)
        {
            lock (gate)
            {
                return rules.Process(notification, (r, _) => Admit(r.Id, false, default),
                cameras.TryGetValue(notification.CameraId, out var entry) && entry.Camera.Enabled &&
                (sourceSnapshot is null || ReferenceEquals(entry.Camera, sourceSnapshot)));
            }
        }
        public void ResetEpoch(CameraConfiguration snapshot)
        {
            lock (gate)
            {
                if (IsCurrentCamera(snapshot))
                {
                    rules.ResetCamera(snapshot.Id);
                }
            }
        }
        public bool ManualTest(string ruleId, CancellationToken cancellation = default)
        {
            lock (gate)
            {
                return Admit(ruleId, true, cancellation);
            }
        }
        private bool Admit(string id, bool manual, CancellationToken cancellation)
        {
            if (!configured.TryGetValue(id, out var entry) || !cameras.TryGetValue(entry.Rule.Match.CameraId, out var source) ||
                !source.Camera.Enabled || !manual && !entry.Rule.Match.Enabled)
            {
                return false;
            }
            var job = new DispatchJob(Guid.NewGuid(), id, entry.Revision, source.Revision, entry.Rule.Action, manual, clock.GetTimestamp(), cancellation);
            // Publish before TrySubmit: execution or cancellation may finish before it returns.
            // Record uses this same lock and checks the job identity.
            status[id] = new(job.Id, id, clock.GetUtcNow(), ExecutionResult.Queued);
            bool accepted;
            try
            {
                accepted = dispatcher?.TrySubmit(job, this) == true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Cancellation before TaskFlow reserves admission is not overflow. Do not
                // resubmit: the cancelled attempt may already own completion middleware.
                status[id] = new(job.Id, id, clock.GetUtcNow(), ExecutionResult.Cancelled);
                return false;
            }
            if (!accepted)
            {
                status[id] = new(job.Id, id, clock.GetUtcNow(), ExecutionResult.Overflow);
            }
            return accepted;
        }
        public bool Eligible(DispatchJob job)
        {
            lock (gate)
            {
                return configured.TryGetValue(job.RuleId, out var entry) &&
                cameras.TryGetValue(entry.Rule.Match.CameraId, out var source) && source.Camera.Enabled && source.Revision == job.CameraRevision &&
                entry.Revision == job.RuleRevision && (job.Manual || entry.Rule.Match.Enabled) &&
                !IsSelfAddress(job.Action.Address);
            }
        }
        public void Record(ExecutionStatus result)
        {
            lock (gate)
            {
                // An older active job must not overwrite a newer admission's status.
                if (status.TryGetValue(result.RuleId, out var current) && current.JobId == result.JobId)
                {
                    status[result.RuleId] = result;
                }
                if (configured.ContainsKey(result.RuleId) && result.Result is not (ExecutionResult.Running or ExecutionResult.Queued))
                {
                    executions[result.RuleId] = result;
                }
            }
        }
    }
}
