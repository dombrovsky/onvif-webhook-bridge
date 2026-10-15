namespace OnvifWebhookBridge.Events
{
    public sealed class EventHistory
    {
        public const int MaximumCount = 100;
        public const int MaximumBytes = 1024 * 1024;
        private readonly object gate = new();
        private readonly Queue<NormalizedEvent> events = new();
        private int bytes;

        public void Add(NormalizedEvent notification)
        {
            ArgumentNullException.ThrowIfNull(notification);
            if (notification.RetainedBytes is < 0 or > EventNormalizer.MaximumEventBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(notification));
            }
            lock (gate)
            {
                events.Enqueue(notification);
                bytes += notification.RetainedBytes;
                while (events.Count > MaximumCount || bytes > MaximumBytes)
                {
                    bytes -= events.Dequeue().RetainedBytes;
                }
            }
        }

        public IReadOnlyList<NormalizedEvent> Snapshot()
        {
            lock (gate)
            {
                return Array.AsReadOnly(events.ToArray());
            }
        }
    }
}
