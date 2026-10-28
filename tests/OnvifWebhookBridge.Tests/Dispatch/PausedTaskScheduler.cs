namespace OnvifWebhookBridge.Tests.Dispatch
{
    // Delay the actual TaskFlow delegate start; dispatched work still holds pending capacity.
    internal sealed class PausedTaskScheduler : TaskScheduler
    {
        private TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Resume() => ready.TrySetResult();
        public void Pause() => ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void QueueTask(Task task)
        {
            var start = ready.Task;
            _ = start.ContinueWith(_ => TryExecuteTask(task), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task>? GetScheduledTasks() => null;
    }
}
