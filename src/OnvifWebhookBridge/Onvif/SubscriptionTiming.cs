namespace OnvifWebhookBridge.Onvif
{
    // All budgets are monotonic. Reserve time for the complete HTTP request, not just the camera's long poll.
    internal static class SubscriptionTiming
    {
        public static double Lease(DateTime current, DateTime termination) => (termination - current).TotalSeconds is var seconds && seconds >= 2
            ? seconds : throw new InvalidOperationException("Unusable lease.");
        public static bool Renew(double remaining) => remaining <= 30;
        public static int PollSeconds(double remaining) => (int)Math.Clamp(Math.Floor(remaining - 20), 1, 20);
        public static TimeSpan Backoff(int failures, double jitter) => TimeSpan.FromSeconds(
            Math.Min(30, Math.Pow(2, Math.Clamp(failures - 1, 0, 5))) * (0.75 + Math.Clamp(jitter, 0, 1) * 0.25));
    }
}
