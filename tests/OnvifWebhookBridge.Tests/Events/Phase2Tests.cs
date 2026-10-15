namespace OnvifWebhookBridge.Tests.Events
{
    using System.Text;
    using System.Xml;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Rules;
    using SharpOnvifCommon.Onvif;
    using Xunit;

    [Trait("Category", "Unit")]
    public sealed class Phase2Tests
    {
        internal static NormalizedEvent Event(string value = "false", string operation = "Changed",
            string identity = "a", string topic = "s:Detection/Active", string? extra = null,
            string timestamp = "2026-01-01T01:02:03Z")
        {
            var document = new XmlDocument();
            document.LoadXml($"""
                <tt:Message xmlns:tt="http://www.onvif.org/ver10/schema" xmlns:x="urn:synthetic:extra"
                    UtcTime="{timestamp}" PropertyOperation="{operation}" x:extra="preserved">
                  <tt:Source><tt:SimpleItem Name="Input" Value="{identity}"/></tt:Source>
                  <tt:Key><tt:SimpleItem Name="Track" Value="synthetic"/></tt:Key>
                  <tt:Data><tt:SimpleItem Name="Active" Value="{value}"/>{extra}</tt:Data>
                  <x:Extension x:code="synthetic">extra</x:Extension>
                </tt:Message>
                """);
            var namespaceAttribute = document.CreateAttribute("xmlns", "s", "http://www.w3.org/2000/xmlns/");
            namespaceAttribute.Value = "urn:synthetic:topics";
            var notification = new NotificationMessageHolderType
            {
                Message = document.DocumentElement!,
                Topic = new TopicExpressionType
                {
                    Any = [document.CreateTextNode(topic)], Dialect = "urn:synthetic:dialect",
                    AnyAttr = [namespaceAttribute]
                }
            };
            return new EventNormalizer().Normalize(notification, "camera", "Synthetic camera", DateTimeOffset.UnixEpoch);
        }

        internal static ForwardingRule Rule(string id = "rule", OccurrenceMode mode = OccurrenceMode.Transition,
            TimeSpan cooldown = default) => new(id, "camera", "s:Detection/Active",
                new(FieldSection.Data, "Active", "true", ComparisonMode.Boolean), true, mode, cooldown);

        [Fact]
        public void PreservesExposedContentDuplicatesAndInvalidOptionalTimestamp()
        {
            var e = Event(extra: """
                <tt:SimpleItem Name="Active" Value="true"/><tt:SimpleItem Name="Missing"/>
                <tt:ElementItem Name="Shape"><x:Shape x:size="3"><x:Point>synthetic</x:Point></x:Shape></tt:ElementItem>
                <x:Unknown Name="Other" Value="opaque"/>
                """, timestamp: "invalid", operation: "VendorOperation");
            Assert.False(e.Truncated);
            Assert.Equal("s:Detection/Active", e.Topic);
            Assert.Equal("urn:synthetic:dialect", e.Dialect);
            Assert.Contains(e.TopicAttributes, a => a.Name == "xmlns:s" && a.Value == "urn:synthetic:topics");
            Assert.Equal(DateTimeOffset.UnixEpoch, e.ReceivedAt);
            Assert.Equal("invalid", e.CameraTimeText);
            Assert.Null(e.CameraTime);
            Assert.Contains("InvalidCameraTimestamp", e.Diagnostics);
            Assert.Equal("VendorOperation", e.PropertyOperation);
            Assert.Equal(5, e.Data.Count);
            Assert.Null(e.Data[2].Value);
            Assert.Contains("x:Shape", e.Data[3].Xml);
            Assert.Contains("x:Extension", e.RawMessageXml);
            Assert.Single(e.Key);
            Assert.True(RuleMatcher.Matches(Rule(), e));
            Assert.NotNull(Event().CameraTime);
        }

        [Fact]
        public void MissingMessageAndMalformedFieldsRemainInspectable()
        {
            var n = new EventNormalizer();
            var absent = n.Normalize(new NotificationMessageHolderType(), "camera", "Synthetic", DateTimeOffset.UnixEpoch);
            Assert.Contains("MissingSchemaMessage", absent.Diagnostics);
            Assert.Null(absent.CameraTimeText);
            var doc = new XmlDocument();
            doc.LoadXml("<Unknown xmlns='urn:synthetic'><Message UtcTime='bad'/></Unknown>");
            var unknown = n.Normalize(new NotificationMessageHolderType { Message = doc.DocumentElement! }, "camera", "Synthetic", DateTimeOffset.UnixEpoch);
            Assert.Contains("Unknown", unknown.RawMessageXml);
            Assert.Empty(unknown.Data);
            Assert.False(RuleMatcher.Matches(Rule(), absent));
            Assert.False(RuleMatcher.Matches(Rule(), Event(extra: "<tt:SimpleItem Value='true'/>")));
        }

        [Theory]
        [InlineData("true", true)] [InlineData("1", true)]
        [InlineData("false", false)] [InlineData("0", false)]
        [InlineData("TRUE", false)] [InlineData(" true ", false)]
        public void BooleanComparisonHasOnlyExplicitLexicalValues(string value, bool expected)
        {
            Assert.Equal(expected, RuleMatcher.Matches(Rule(), Event(value)));
            var text = Rule() with { Condition = new(FieldSection.Data, "Active", "true") };
            Assert.Equal(value == "true", RuleMatcher.Matches(text, Event(value)));
        }

        [Fact]
        public void ExactMatchingDistinguishesAliasesCaseSectionsAndMissingFields()
        {
            var e = Event("true");
            Assert.False(RuleMatcher.Matches(Rule(), e with { Topic = "alias:Detection/Active" }));
            Assert.False(RuleMatcher.Matches(Rule(), e with { Topic = "s:detection/Active" }));
            Assert.False(RuleMatcher.Matches(Rule(), e with { CameraId = "Camera" }));
            Assert.False(RuleMatcher.Matches(Rule() with { Condition = new(FieldSection.Source, "Active", "true") }, e));
            Assert.False(RuleMatcher.Matches(Rule() with { Condition = new(FieldSection.Data, "active", "true") }, e));
            Assert.True(RuleMatcher.Matches(Rule() with { Condition = null }, Event("false")));
            var collision = e with { Source = Array.AsReadOnly(new[] { new EventField("SimpleItem", "Active", "false", "") }) };
            Assert.True(RuleMatcher.Matches(Rule(), collision));
            var lookalike = Event(extra: "<SimpleItem xmlns='urn:synthetic:vendor' Name='Active' Value='true'/>");
            Assert.False(RuleMatcher.Matches(Rule(), lookalike));
            Assert.False(RuleMatcher.Matches(Rule(), Event(extra: "<tt:ElementItem Name='Active' Value='true'/>")));
        }

        [Fact]
        public void TransitionCyclesAndPropertyOperationsHaveDeterministicDecisions()
        {
            var state = new RuleState(new EventHistory());
            state.SetRules([Rule()]);
            Assert.Equal(new[] { DecisionKind.Baseline, DecisionKind.Accepted, DecisionKind.Unchanged,
                DecisionKind.Unmatched, DecisionKind.Accepted, DecisionKind.Baseline, DecisionKind.Unchanged,
                DecisionKind.Deleted, DecisionKind.Baseline, DecisionKind.Unmatched, DecisionKind.Accepted },
                new[] { Event(), Event("true"), Event("1"), Event(), Event("true"),
                    Event("true", "Initialized"), Event("true"), Event("false", "Deleted"),
                    Event("true"), Event(), Event("true", "Unknown") }
                .Select(e => state.Process(e, (_, _) => true).Single().Kind));
        }

        [Fact]
        public void HistoryPrecedesAdmissionAndIndependentRulesSurviveRejection()
        {
            var history = new EventHistory();
            var state = new RuleState(history);
            state.SetRules([Rule("first", OccurrenceMode.Occurrence), Rule("second", OccurrenceMode.Occurrence), Rule("disabled") with { Enabled = false }]);
            var decisions = state.Process(Event("true", "Initialized"), (r, e) =>
            {
                Assert.Same(e, history.Snapshot().Last());
                if (r.Id == "first") { throw new InvalidOperationException("synthetic-secret"); }
                return true;
            });
            Assert.Equal(new[] { DecisionKind.Rejected, DecisionKind.Accepted, DecisionKind.Disabled }, decisions.Select(d => d.Kind));
            state.Process(Event(), (_, _) => false);
            state.Process(Event(topic: "s:Unmatched"), (_, _) => false);
            state.Process(Event("true", "Deleted"), (_, _) => true);
            Assert.Equal(4, history.Snapshot().Count);
        }

        [Fact]
        public void CooldownUsesMonotonicAcceptanceAndDoesNotReplaySuppressedTransitions()
        {
            var clock = new Clock();
            var state = new RuleState(new EventHistory(), clock);
            state.SetRules([Rule(cooldown: TimeSpan.FromSeconds(10))]);
            Assert.Equal(DecisionKind.Baseline, Decide(Event()));
            Assert.Equal(DecisionKind.Rejected, state.Process(Event("true"), (_, _) => false).Single().Kind);
            Assert.Equal(DecisionKind.Unchanged, Decide(Event("true")));
            Decide(Event());
            Assert.Equal(DecisionKind.Accepted, Decide(Event("true")));
            clock.Now = 9;
            Decide(Event());
            Assert.Equal(DecisionKind.Cooldown, Decide(Event("true")));
            clock.Now = 10;
            Assert.Equal(DecisionKind.Unchanged, Decide(Event("true")));
            Decide(Event());
            Assert.Equal(DecisionKind.Accepted, Decide(Event("true")));
            DecisionKind Decide(NormalizedEvent e) => state.Process(e, (_, _) => true).Single().Kind;
        }

        [Fact]
        public void OccurrenceRejectionDoesNotConsumeCooldownAndCooldownSpansIdentities()
        {
            var clock = new Clock();
            var state = new RuleState(new EventHistory(), clock);
            state.SetRules([Rule(mode: OccurrenceMode.Occurrence, cooldown: TimeSpan.FromSeconds(10))]);
            Assert.Equal(DecisionKind.Rejected, state.Process(Event("true"), (_, _) => false).Single().Kind);
            Assert.Equal(DecisionKind.Accepted, state.Process(Event("true"), (_, _) => true).Single().Kind);
            Assert.Equal(DecisionKind.Cooldown, state.Process(Event("true", identity: "b"), (_, _) => true).Single().Kind);
            clock.Now = 10;
            Assert.Equal(DecisionKind.Accepted, state.Process(Event("true", "Deleted"), (_, _) => true).Single().Kind);
        }

        [Fact]
        public void IdentityEvictionAndEpochRuleAndEnableChangesResetBaselines()
        {
            var state = new RuleState(new EventHistory(), identityCapacity: 2);
            var rule = Rule();
            state.SetRules([rule]);
            Decide(Event(identity: "a"));
            Decide(Event(identity: "b"));
            Assert.Equal(DecisionKind.Accepted, Decide(Event("true", identity: "a")));
            Decide(Event(identity: "c")); // FIFO, not refresh-on-read: a is evicted.
            Assert.Equal(DecisionKind.Baseline, Decide(Event("true", identity: "a")));
            state.ResetCamera("other");
            Assert.Equal(DecisionKind.Unmatched, Decide(Event()));
            state.ResetCamera("camera");
            Assert.Equal(DecisionKind.Baseline, Decide(Event("true")));
            state.SetRules([rule]); // identical configuration does not erase state
            Assert.Equal(DecisionKind.Unchanged, Decide(Event("true")));
            state.SetRules([rule with { Cooldown = TimeSpan.FromSeconds(1) }]);
            Assert.Equal(DecisionKind.Baseline, Decide(Event("true")));
            state.SetRules([rule with { Enabled = false }]);
            Assert.Equal(DecisionKind.Disabled, Decide(Event()));
            state.SetRules([rule]);
            Assert.Equal(DecisionKind.Baseline, Decide(Event("true")));
            Assert.Equal(DecisionKind.Disabled, state.Process(Event(), (_, _) => true, cameraEnabled: false).Single().Kind);
            DecisionKind Decide(NormalizedEvent e) => state.Process(e, (_, _) => true).Single().Kind;
        }

        [Fact]
        public void SourceConditionIsExcludedAndKeysDuplicatesRemainIdentity()
        {
            var state = new RuleState(new EventHistory());
            state.SetRules([Rule() with { Condition = new(FieldSection.Source, "Input", "b") }]);
            Assert.Equal(DecisionKind.Baseline, state.Process(Event(identity: "a"), (_, _) => true).Single().Kind);
            Assert.Equal(DecisionKind.Accepted, state.Process(Event(identity: "b"), (_, _) => true).Single().Kind);
            var changedKey = Event(identity: "a") with { Key = Array.AsReadOnly(new[] { new EventField("SimpleItem", "Track", "different", "") }) };
            Assert.Equal(DecisionKind.Baseline, state.Process(changedKey, (_, _) => true).Single().Kind);
            Assert.Equal(DecisionKind.Accepted, state.Process(changedKey with { Source = Event(identity: "b").Source }, (_, _) => true).Single().Kind);
        }

        [Fact]
        public void IdentityIgnoresFieldOrderButPreservesDuplicateAndExtensionIdentity()
        {
            var state = new RuleState(new EventHistory());
            state.SetRules([Rule()]);
            var e = Event();
            var fields = new[] { e.Source[0], new EventField("SimpleItem", "Other", "synthetic", "") };
            e = e with { Source = Array.AsReadOnly(fields) };
            state.Process(e, (_, _) => true);
            var on = Event("true") with { Source = Array.AsReadOnly(fields.Reverse().ToArray()) };
            Assert.Equal(DecisionKind.Accepted, state.Process(on, (_, _) => true).Single().Kind);
            var duplicate = on with { Source = Array.AsReadOnly(new[] { fields[0], fields[1], fields[1] }) };
            Assert.Equal(DecisionKind.Baseline, state.Process(duplicate, (_, _) => true).Single().Kind);
            var extended = on with { Source = Array.AsReadOnly(new[] { fields[0] with { IdentityMetadata = "synthetic-extra" }, fields[1] }) };
            Assert.Equal(DecisionKind.Baseline, state.Process(extended, (_, _) => true).Single().Kind);
        }

        [Fact]
        public void TruncationRemainsInHistoryAndInvalidatesUncertainTransitionBaselines()
        {
            var history = new EventHistory();
            var state = new RuleState(history);
            state.SetRules([Rule()]);
            state.Process(Event(), (_, _) => true);
            Assert.Equal(DecisionKind.UnsafeContent, state.Process(Event(new string('a', 100_000)), (_, _) => true).Single().Kind);
            Assert.Equal(DecisionKind.Baseline, state.Process(Event("true"), (_, _) => true).Single().Kind);
            Assert.True(history.Snapshot()[1].Truncated);
        }

        [Fact]
        public void NormalizerPreservesAdditionalIdentityAttributesWithoutDependingOnAttributeOrder()
        {
            var normalizer = new EventNormalizer();
            NormalizedEvent WithSource(string source)
            {
                var document = new XmlDocument();
                document.LoadXml($"<Message xmlns='http://www.onvif.org/ver10/schema' xmlns:x='urn:synthetic'><Source>{source}</Source></Message>");
                return normalizer.Normalize(new NotificationMessageHolderType { Message = document.DocumentElement! }, "camera", "Synthetic", DateTimeOffset.UnixEpoch);
            }
            var first = WithSource("<SimpleItem Name='Input' Value='a' x:code='one' x:other='two'><x:Extra/></SimpleItem>");
            var reordered = WithSource("<SimpleItem x:other='two' x:code='one' Value='a' Name='Input'><x:Extra/></SimpleItem>");
            var changed = WithSource("<SimpleItem Name='Input' Value='a' x:code='different' x:other='two'><x:Extra/></SimpleItem>");
            Assert.Equal(first.Source[0].IdentityMetadata, reordered.Source[0].IdentityMetadata);
            Assert.NotEqual(first.Source[0].IdentityMetadata, changed.Source[0].IdentityMetadata);
            Assert.Contains("Extra", first.Source[0].IdentityMetadata);
        }

        [Fact]
        public void SimultaneousAdmissionsAreAtomic()
        {
            var state = new RuleState(new EventHistory(), new Clock());
            state.SetRules([Rule(mode: OccurrenceMode.Occurrence, cooldown: TimeSpan.FromSeconds(10))]);
            var accepted = 0;
            Parallel.For(0, 100, _ => state.Process(Event("true"), (_, _) => { Interlocked.Increment(ref accepted); return true; }));
            Assert.Equal(1, accepted);
            state.SetRules([Rule()]);
            state.Process(Event(), (_, _) => true);
            accepted = 0;
            Parallel.For(0, 100, _ => state.Process(Event("true"), (_, _) => { Interlocked.Increment(ref accepted); return true; }));
            Assert.Equal(1, accepted);
        }

        [Fact]
        public void HistoryAndNormalizationBoundBytesCountFieldsAndUnicode()
        {
            var history = new EventHistory();
            for (var i = 0; i < 110; i++) { history.Add(Event(i.ToString())); }
            Assert.Equal(100, history.Snapshot().Count);
            Assert.Equal("10", history.Snapshot()[0].Data[0].Value);
            var huge = Event(new string('x', 100_000));
            Assert.True(huge.Truncated);
            Assert.Contains("ContentTruncated", huge.Diagnostics);
            Assert.True(huge.RetainedBytes <= EventNormalizer.MaximumEventBytes);
            Assert.True(Encoding.UTF8.GetByteCount(huge.RawMessageXml) <= EventNormalizer.MaximumStringBytes);
            Assert.False(RuleMatcher.Matches(Rule() with { Condition = null }, huge));
            for (var i = 0; i < 110; i++) { history.Add(huge); }
            Assert.True(history.Snapshot().Sum(e => e.RetainedBytes) <= EventHistory.MaximumBytes);
            var many = Event(extra: string.Concat(Enumerable.Repeat("<tt:SimpleItem Name='a' Value='b'/>", 300)));
            Assert.True(many.Truncated);
            Assert.True(many.Source.Count + many.Key.Count + many.Data.Count <= EventNormalizer.MaximumFields);
            var unicode = Event(string.Concat(Enumerable.Repeat("🙂", 5000)));
            Assert.True(unicode.Truncated);
            Assert.DoesNotContain("�", unicode.Data[0].Value!);
        }

        [Fact]
        public void InvalidRulesAreRejectedWithoutChangingExistingRules()
        {
            var state = new RuleState(new EventHistory());
            state.SetRules([Rule()]);
            Assert.Throws<ArgumentException>(() => state.SetRules([Rule() with { Condition = null }]));
            Assert.Throws<ArgumentException>(() => state.SetRules([Rule(), Rule()]));
            Assert.Throws<ArgumentException>(() => state.SetRules([Rule() with { Cooldown = TimeSpan.FromSeconds(-1) }]));
            Assert.Throws<ArgumentException>(() => state.SetRules([Rule() with { Condition = new(FieldSection.Data, "Active", "yes", ComparisonMode.Boolean) }]));
            Assert.Equal(DecisionKind.Baseline, state.Process(Event(), (_, _) => true).Single().Kind);
        }

        internal sealed class Clock : TimeProvider
        {
            public long Now { get; set; }
            public override long TimestampFrequency => 1;
            public override long GetTimestamp() => Now;
        }
    }
}
