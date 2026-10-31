namespace OnvifWebhookBridge.Tests.Dispatch
{
    // Timer callbacks run synchronously on Advance; continuations retain normal task scheduling.
    internal sealed class TestClock : TimeProvider
    {
        private long ticks;
        private readonly List<Timer> timers = [];
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitForTimer(TimeSpan due)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task next;
                lock (timers)
                {
                    if (timers.Any(t => t.Due == ticks + due.Ticks))
                    {
                        return;
                    }
                    next = changed.Task;
                }
                await next.WaitAsync(deadline.Token);
            }
        }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (timers)
            {
                var timer = new Timer(this, callback, state);
                timer.Change(dueTime, period);
                timers.Add(timer);
                changed.TrySetResult();
                changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return timer;
            }
        }
        public void Advance(TimeSpan elapsed)
        {
            Timer[] due;
            lock (timers)
            {
                Interlocked.Add(ref ticks, elapsed.Ticks);
                due = timers.Where(t => t.Due <= ticks).ToArray();
                foreach (var timer in due)
                {
                    timer.Due = long.MaxValue;
                }
            }
            foreach (var timer in due)
            {
                timer.Callback(timer.State);
            }
        }
        private sealed class Timer(TestClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; } = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.timers)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                    return true;
                }
            }
            public void Dispose()
            {
                lock (owner.timers)
                {
                    owner.timers.Remove(this);
                    Due = long.MaxValue;
                }
            }
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
