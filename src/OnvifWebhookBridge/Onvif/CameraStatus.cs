namespace OnvifWebhookBridge.Onvif
{
    // Contains identifiers, fixed categories and timestamps only; never network exception text.
    public sealed record CameraStatus(string CameraId, string State = "Disabled", DateTimeOffset? LastContact = null,
        DateTimeOffset? LastEvent = null, long Epoch = 0, int Failures = 0);
}
