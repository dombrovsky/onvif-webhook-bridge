namespace OnvifWebhookBridge.Rules
{
    using OnvifWebhookBridge.Events;
    using System.Text;

    // Owns history-before-matching and atomic state/admission. No worker or dispatcher.
    public sealed class RuleState(EventHistory history, TimeProvider? timeProvider = null, int identityCapacity = 1024)
    {
        public const int MaximumRules = 256;
        private readonly object gate = new();
        private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
        private readonly int capacity = identityCapacity is > 0 and <= 1024 ? identityCapacity : throw new ArgumentOutOfRangeException(nameof(identityCapacity));
        private ForwardingRule[] rules = [];
        private readonly Dictionary<(string Rule, string Identity), bool> states = new();
        private readonly LinkedList<(string Rule, string Identity)> order = new();
        private readonly Dictionary<string, long> acceptedAt = new(StringComparer.Ordinal);

        public void SetRules(IEnumerable<ForwardingRule> configured)
        {
            var replacement = configured.Take(MaximumRules + 1).ToArray();
            if (replacement.Length > MaximumRules || replacement.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != replacement.Length)
            {
                throw new ArgumentException("Rule count or duplicate ID is invalid.", nameof(configured));
            }
            foreach (var rule in replacement)
            {
                if (string.IsNullOrEmpty(rule.Id) || string.IsNullOrEmpty(rule.CameraId) || string.IsNullOrEmpty(rule.Topic) ||
                    rule.Id.Length > 4096 || rule.CameraId.Length > 4096 || rule.Topic.Length > 4096 ||
                    rule.Cooldown < TimeSpan.Zero || !Enum.IsDefined(rule.Mode) ||
                    rule.Mode == OccurrenceMode.Transition && rule.Condition is null ||
                    rule.Condition is { } f && (string.IsNullOrEmpty(f.Name) || f.Name.Length > 4096 ||
                        f.ExpectedValue.Length > 4096 || !Enum.IsDefined(f.Section) || !Enum.IsDefined(f.Comparison) ||
                        f.Comparison == ComparisonMode.Boolean && RuleMatcher.BooleanValue(f.ExpectedValue) is null))
                {
                    throw new ArgumentException("Invalid rule configuration.", nameof(configured));
                }
            }
            lock (gate)
            {
                foreach (var old in rules)
                {
                    if (!replacement.Contains(old))
                    {
                        ClearRule(old.Id);
                    }
                }
                rules = replacement;
            }
        }

        public void ResetCamera(string cameraId)
        {
            lock (gate)
            {
                foreach (var rule in rules.Where(r => r.CameraId == cameraId))
                {
                    ClearIdentities(rule.Id);
                }
            }
        }

        internal void ResetRule(string ruleId)
        {
            lock (gate)
            {
                ClearRule(ruleId);
            }
        }

        // The callback must be bounded, synchronous and non-reentrant. True means accepted
        // into a future queue (or a test recorder). Throwing is a rejection, isolated per rule.
        public IReadOnlyList<RuleDecision> Process(NormalizedEvent notification,
            Func<ForwardingRule, NormalizedEvent, bool> admit, bool cameraEnabled = true)
        {
            lock (gate)
            {
                history.Add(notification);
                var decisions = new List<RuleDecision>();
                foreach (var rule in rules)
                {
                    decisions.Add(new(rule.Id, Decide(rule)));
                }
                return decisions.AsReadOnly();

                DecisionKind Decide(ForwardingRule rule)
                {
                    if (!cameraEnabled || !rule.Enabled)
                    {
                        return DecisionKind.Disabled;
                    }
                    if (notification.Truncated)
                    {
                        if (rule.CameraId == notification.CameraId)
                        {
                            ClearIdentities(rule.Id);
                        }
                        return DecisionKind.UnsafeContent;
                    }
                    if (rule.CameraId != notification.CameraId || rule.Topic != notification.Topic)
                    {
                        return DecisionKind.Unmatched;
                    }
                    var matching = RuleMatcher.Matches(rule, notification);
                    if (rule.Mode == OccurrenceMode.Transition)
                    {
                        var identity = (rule.Id, Identity(notification, rule.Condition!));
                        if (notification.PropertyOperation == "Deleted")
                        {
                            states.Remove(identity);
                            order.Remove(identity);
                            return DecisionKind.Deleted;
                        }
                        var known = states.TryGetValue(identity, out var previous);
                        if (!known && states.Count == capacity)
                        {
                            states.Remove(order.First!.Value);
                            order.RemoveFirst();
                        }
                        if (!known)
                        {
                            order.AddLast(identity);
                        }
                        states[identity] = matching;
                        if (!known || notification.PropertyOperation == "Initialized")
                        {
                            return DecisionKind.Baseline;
                        }
                        if (!matching)
                        {
                            return DecisionKind.Unmatched;
                        }
                        if (previous)
                        {
                            return DecisionKind.Unchanged;
                        }
                    }
                    else if (!matching)
                    {
                        return DecisionKind.Unmatched;
                    }
                    var now = clock.GetTimestamp();
                    if (acceptedAt.TryGetValue(rule.Id, out var last) && clock.GetElapsedTime(last, now) < rule.Cooldown)
                    {
                        return DecisionKind.Cooldown;
                    }
                    bool accepted;
                    try
                    {
                        accepted = admit(rule, notification);
                    }
                    catch { accepted = false; }
                    if (!accepted)
                    {
                        return DecisionKind.Rejected;
                    }
                    acceptedAt[rule.Id] = clock.GetTimestamp();
                    return DecisionKind.Accepted;
                }
            }
        }

        private void ClearRule(string id)
        {
            ClearIdentities(id);
            acceptedAt.Remove(id);
        }
        private void ClearIdentities(string id)
        {
            foreach (var identity in states.Keys.Where(k => k.Rule == id).ToArray())
            {
                states.Remove(identity);
                order.Remove(identity);
            }
        }

        private static string Identity(NormalizedEvent notification, FieldCondition condition)
        {
            // Length framing prevents delimiter collisions; sorting preserves multiplicity
            // while making collection order irrelevant. Structured/extension items use XML.
            var text = new StringBuilder();
            foreach (var section in new[] { notification.Source, notification.Key })
            {
                var selected = ReferenceEquals(section, notification.Source) && condition.Section == FieldSection.Source;
                foreach (var field in section.Where(f => !(selected && f.Kind == "SimpleItem" && f.Name == condition.Name))
                    .Select(f => Encode(f.Kind) + Encode(f.Name) + Encode(f.Value) + Encode(f.IdentityMetadata) +
                        (f.Kind == "SimpleItem" ? "" : Encode(f.Xml))).Order(StringComparer.Ordinal))
                {
                    text.Append(Encode(field));
                }
                text.Append(';');
            }
            // Camera/topic already partitioned by the immutable rule.
            return text.ToString();
        }
        private static string Encode(string? value) => value is null ? "-1:" : $"{value.Length}:{value}";
    }
}
