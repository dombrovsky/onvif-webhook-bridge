namespace OnvifWebhookBridge.Tests.Dispatch
{
    // Delay the actual TaskFlow delegate start; dispatched work still holds pending capacity.
    internal sealed class PausedTaskScheduler : TaskScheduler
    {
        private TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        public void Resume()
        {
            lock (gate)
            {
                ready.TrySetResult();
            }
        }
        public void Pause()
        {
            lock (gate)
            {
                if (ready.Task.IsCompleted)
                {
                    ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
        protected override void QueueTask(Task task)
        {
            Task start;
            lock (gate)
            {
                start = ready.Task;
            }
            _ = start.ContinueWith(_ => TryExecuteTask(task), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task>? GetScheduledTasks() => null;
    }
}
