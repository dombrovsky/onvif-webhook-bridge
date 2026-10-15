namespace OnvifWebhookBridge.Web
{
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Onvif;
    using OnvifWebhookBridge.Persistence;
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
    [JsonSerializable(typeof(LoginRequest))]
    [JsonSerializable(typeof(SessionView))]
    [JsonSerializable(typeof(ApiError))]
    [JsonSerializable(typeof(RevisionView))]
    [JsonSerializable(typeof(RevisionRequest))]
    [JsonSerializable(typeof(EnabledRequest))]
    [JsonSerializable(typeof(CameraRequest))]
    [JsonSerializable(typeof(RuleRequest))]
    [JsonSerializable(typeof(ConfigurationView))]
    [JsonSerializable(typeof(CameraView[]))]
    [JsonSerializable(typeof(RuleView[]))]
    [JsonSerializable(typeof(CameraStatus[]))]
    [JsonSerializable(typeof(ExecutionStatus[]))]
    [JsonSerializable(typeof(NormalizedEvent[]))]
    [JsonSerializable(typeof(ConnectivityView))]
    public partial class JsonContext : JsonSerializerContext;
}
