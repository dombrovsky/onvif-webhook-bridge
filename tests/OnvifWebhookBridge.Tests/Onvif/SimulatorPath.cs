namespace OnvifWebhookBridge.Tests.Onvif
{
    using System.Net;
    using System.Net.Http.Json;
    using System.Text.Json.Serialization;
    using System.Xml;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Onvif;
    using OnvifWebhookBridge.Rules;
    using SharpOnvifClient;
    using SharpOnvifClient.Events;
    using SharpOnvifCommon.Security;
    using SharpOnvifCommon.Soap;

    // Shared by xUnit and the published native runner; no alternate ONVIF fixture.
    internal static class SimulatorPath
    {
        // Unit fixture coverage executed by both JIT tests and the native runner.
        // It is deliberately separate from the actual onvifsim protocol check below.
        public static void VerifySyntheticContent()
        {
            var document = new XmlDocument();
            document.LoadXml("""
                <tt:Message xmlns:tt="http://www.onvif.org/ver10/schema" xmlns:x="urn:synthetic:extension" UtcTime="invalid" PropertyOperation="VendorOperation">
                  <tt:Source><tt:SimpleItem Name="Input" Value="synthetic" x:extra="retained"/></tt:Source>
                  <tt:Data><tt:SimpleItem Name="Flag" Value="false"/><tt:SimpleItem Name="Flag" Value="1"/>
                    <tt:ElementItem Name="Shape"><x:Shape>synthetic</x:Shape></tt:ElementItem><tt:SimpleItem Name="Missing"/></tt:Data>
                  <x:Extension>retained</x:Extension>
                </tt:Message>
                """);
            var n = new SharpOnvifCommon.Onvif.NotificationMessageHolderType
            {
                Message = document.DocumentElement!,
                Topic = new SharpOnvifCommon.Onvif.TopicExpressionType { Any = [document.CreateTextNode("s:Unit/Flag")] }
            };
            var e = new EventNormalizer().Normalize(n, "camera", "Synthetic", DateTimeOffset.UnixEpoch);
            Check(!e.Truncated && e.CameraTime is null && e.CameraTimeText == "invalid", "UnitTimestamp");
            Check(e.Data.Count == 4 && e.Data[3].Value is null && e.Data[2].Xml.Contains("Shape", StringComparison.Ordinal), "UnitStructuredDuplicates");
            Check(e.RawMessageXml.Contains("Extension", StringComparison.Ordinal) && e.Source[0].IdentityMetadata.Length > 0, "UnitExtension");
            var rule = new ForwardingRule("unit", "camera", e.Topic, new(FieldSection.Data, "Flag", "true", ComparisonMode.Boolean));
            Check(RuleMatcher.Matches(rule, e), "UnitBooleanDuplicate");
            Check(!RuleMatcher.Matches(rule with { Topic = "alias:Unit/Flag" }, e), "UnitAliasExact");
            var history = new EventHistory();
            for (var i = 0; i < 110; i++) { history.Add(e); }
            Check(history.Snapshot().Count == 100, "UnitHistoryCount");
            document.DocumentElement!.InnerText = new string('x', 100_000);
            var large = new EventNormalizer().Normalize(n, "camera", "Synthetic", DateTimeOffset.UnixEpoch);
            Check(large.Truncated && large.RetainedBytes <= EventNormalizer.MaximumEventBytes, "UnitXmlCap");
            for (var i = 0; i < 100; i++) { history.Add(large); }
            Check(history.Snapshot().Sum(item => item.RetainedBytes) <= EventHistory.MaximumBytes, "UnitHistoryBytes");
        }

        public static async Task Verify(Uri endpoint, Uri controlAddress, CancellationToken token)
        {
            const string username = "synthetic-user";
            const string password = "synthetic-password";
            using var control = new HttpClient { BaseAddress = controlAddress, Timeout = TimeSpan.FromSeconds(10) };
            var scenario = new Scenario(1, new(IPAddress.Any.ToString(), IPAddress.Any.ToString(), false),
                [new("synthetic-camera", "Synthetic integration camera", "generic",
                    [new(username, password, "Administrator")],
                    new(IPAddress.Any.ToString(), 8000, endpoint.Host, endpoint.Port))]);
            using var created = await Post(control, "api/scenario", scenario, SimulatorJson.Default.Scenario, token);
            Check(created.IsSuccessStatusCode, "ScenarioSetup");
            var authentication = new OnvifAuthenticationSettings(DigestAuthentication.HttpDigest | DigestAuthentication.WsUsernameToken);
            using var client = new SimpleOnvifClient(endpoint.AbsoluteUri, username, password, authentication);
            await client.GetServicesAsync().WaitAsync(token);
            var subscription = await client.PullPointSubscribeAsync(120).WaitAsync(token);
            var address = subscription.SubscriptionReference?.Address?.Value;
            Check(!string.IsNullOrEmpty(address), "SubscriptionAddress");
            try
            {
                using var manager = new SubscriptionManagerClient(address!, new OnvifClientSettings
                {
                    Credentials = new NetworkCredential(username, password),
                    Authentication = new AddressedAuthentication(authentication, address!),
                    Timeout = TimeSpan.FromSeconds(10)
                });
                var renewed = await manager.RenewAsync(new RenewRequest { TerminationTime = "PT120S" }, token);
                Check(renewed.TerminationTime > renewed.CurrentTime, "RenewalLease");
                const string topic = "Synthetic/Phase2/Detection";
                var history = new EventHistory();
                var clock = new MonotonicClock();
                var rules = new RuleState(history, clock);
                var condition = new FieldCondition(FieldSection.Data, "State", "true", ComparisonMode.Boolean);
                // State is onvifsim's documented fallback, not a production schema assumption.
                var transition = new ForwardingRule("transition", "camera", topic, condition, true, OccurrenceMode.Transition);
                var occurrence = new ForwardingRule("occurrence", "camera", topic, condition, true,
                    OccurrenceMode.Occurrence, TimeSpan.FromSeconds(10));
                rules.SetRules([transition, occurrence]);
                var normalizer = new EventNormalizer();
                var expected = new[] { DecisionKind.Baseline, DecisionKind.Accepted, DecisionKind.Unchanged, DecisionKind.Unmatched, DecisionKind.Accepted };
                var occurrenceExpected = new[] { DecisionKind.Unmatched, DecisionKind.Accepted, DecisionKind.Cooldown, DecisionKind.Unmatched, DecisionKind.Accepted };
                NormalizedEvent? last = null;
                var index = 0;
                foreach (var value in new[] { false, true, true, false, true })
                {
                    clock.Now = index == 4 ? 10 : 0;
                    using var triggered = await Post(control, "api/cameras/synthetic-camera/events",
                        new Trigger(topic, value, 0), SimulatorJson.Default.Trigger, token);
                    Check(triggered.IsSuccessStatusCode, "EventTrigger");
                    var found = false;
                    while (!found)
                    {
                        var batch = await client.PullPointPullMessagesAsync(address!, 1, 100).WaitAsync(token);
                        foreach (var notification in batch.NotificationMessage ?? [])
                        {
                            var e = normalizer.Normalize(notification, "camera", "Synthetic camera", DateTimeOffset.UtcNow);
                            var decisions = rules.Process(e, (_, candidate) =>
                            {
                                Check(ReferenceEquals(history.Snapshot().Last(), candidate), "HistoryBeforeAdmission");
                                return true;
                            });
                            if (e.Topic != topic) { continue; }
                            Check(!e.Truncated && e.CameraTime is not null && e.RawMessageXml.Contains("SimpleItem", StringComparison.Ordinal), "Preservation");
                            Check(e.Data.Any(f => f.Name == "State" && f.Value == (value ? "true" : "false")), "RawState");
                            Check(decisions[0].Kind == expected[index], "TransitionDecision");
                            Check(decisions[1].Kind == occurrenceExpected[index], "OccurrenceDecision");
                            last = e;
                            found = true;
                        }
                    }
                    index++;
                }
                Check(last is not null && history.Snapshot().Count >= 5, "HistoryIncludesFalseAndRepeats");
                rules.ResetCamera("camera");
                Check(rules.Process(last!, (_, _) => true)[0].Kind == DecisionKind.Baseline, "EpochBaseline");
                Check(!RuleMatcher.Matches(transition with { Topic = topic.ToLowerInvariant() }, last!), "ExactTopic");
                Check(!RuleMatcher.Matches(transition with { Condition = condition with { Comparison = ComparisonMode.Text, ExpectedValue = "1" } }, last!), "TextMode");
                Check(RuleMatcher.Matches(transition with { Condition = condition with { ExpectedValue = "1" } }, last!), "BooleanMode");
            }
            finally { await client.PullPointUnsubscribeAsync(address!).WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        private static void Check(bool condition, string category)
        {
            if (!condition) { throw new InvalidOperationException(category); }
        }
        private static async Task<HttpResponseMessage> Post<T>(HttpClient client, string path, T body,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> metadata, CancellationToken token)
        {
            using var content = JsonContent.Create(body, metadata);
            await content.LoadIntoBufferAsync(token);
            return await client.PostAsync(path, content, token);
        }
        private sealed class MonotonicClock : TimeProvider
        {
            public long Now { get; set; }
            public override long TimestampFrequency => 1;
            public override long GetTimestamp() => Now;
        }
    }

    internal sealed record Scenario(int FormatVersion, ScenarioConfig Config, ScenarioCamera[] Cameras);
    internal sealed record ScenarioConfig(string BindAddress, string ControlApiAddress, bool DiscoveryEnabled);
    internal sealed record ScenarioCamera(string Id, string DisplayName, string Persona, ScenarioUser[] Users, ScenarioNetwork Network);
    internal sealed record ScenarioUser(string Username, string Password, string Level);
    internal sealed record ScenarioNetwork(string BindAddress, int HttpPort, string AdvertisedHost, int AdvertisedHttpPort);
    internal sealed record Trigger(string Topic, bool State, int Duration);
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Scenario))]
    [JsonSerializable(typeof(Trigger))]
    internal partial class SimulatorJson : JsonSerializerContext;
}
