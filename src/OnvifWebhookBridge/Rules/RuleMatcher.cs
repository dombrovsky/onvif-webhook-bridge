namespace OnvifWebhookBridge.Rules
{
    using OnvifWebhookBridge.Events;

    public static class RuleMatcher
    {
        public static bool Matches(ForwardingRule rule, NormalizedEvent notification)
        {
            if (notification.Truncated || rule.CameraId != notification.CameraId || rule.Topic != notification.Topic)
            {
                return false;
            }
            if (rule.Condition is not { } condition)
            {
                return true;
            }
            var fields = condition.Section == FieldSection.Source ? notification.Source : notification.Data;
            return fields.Any(f => f.Kind == "SimpleItem" && f.Name == condition.Name && f.Value is not null &&
                (condition.Comparison == ComparisonMode.Text ? f.Value == condition.ExpectedValue :
                    BooleanValue(f.Value) is { } actual && BooleanValue(condition.ExpectedValue) == actual));
        }

        internal static bool? BooleanValue(string value) => value switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null
        };
    }
}
