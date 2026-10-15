namespace OnvifWebhookBridge.Dispatch
{
    // Round-robin by rule, FIFO within a rule. Admission never waits for network work.
    public sealed class DispatchQueue
    {
        private readonly object gate = new();
        private readonly Dictionary<string, Queue<DispatchJob>> queues = new(StringComparer.Ordinal);
        private readonly LinkedList<string> rotation = new();
        private readonly HashSet<string> active = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim changed = new(0, 1);
        private int count;
        private bool stopped;
        public const int Capacity = 100;
        public const int PerRuleCapacity = 25;
        private long rejected;
        public long Rejected { get { lock (gate) { return rejected; } } }

        public bool TryEnqueue(DispatchJob job)
        {
            lock (gate)
            {
                if (stopped || count == Capacity || queues.TryGetValue(job.RuleId, out var existing) && existing.Count == PerRuleCapacity)
                { rejected++; return false; }
                if (!queues.TryGetValue(job.RuleId, out var queue))
                { queue = new(); queues.Add(job.RuleId, queue); rotation.AddLast(job.RuleId); }
                queue.Enqueue(job); count++; Signal(); return true;
            }
        }

        public async Task<DispatchJob> TakeAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    var node = rotation.First;
                    while (node is not null)
                    {
                        var next = node.Next;
                        if (!active.Contains(node.Value))
                        {
                            var id = node.Value;
                            var queue = queues[id];
                            var job = queue.Dequeue(); count--; active.Add(id);
                            rotation.Remove(node);
                            if (queue.Count == 0) { queues.Remove(id); }
                            else { rotation.AddLast(id); }
                            Signal(); return job;
                        }
                        node = next;
                    }
                }
                await changed.WaitAsync(token);
            }
        }

        public void Complete(DispatchJob job) { lock (gate) { active.Remove(job.RuleId); Signal(); } }
        public IReadOnlyList<DispatchJob> Stop()
        {
            lock (gate)
            {
                stopped = true;
                var abandoned = queues.Values.SelectMany(q => q).ToArray();
                queues.Clear(); rotation.Clear(); count = 0; Signal(); return abandoned;
            }
        }
        private void Signal() { if (changed.CurrentCount == 0) { changed.Release(); } }
    }
}
