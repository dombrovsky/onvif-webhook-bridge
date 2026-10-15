namespace OnvifWebhookBridge.Onvif
{
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;

    public sealed class CameraSupervisor : BackgroundService
    {
        private readonly CameraPipeline pipeline;
        private readonly Func<CameraConfiguration, Action<CameraStatus>, CancellationToken, Task> run;
        private readonly object gate = new();
        private readonly Dictionary<string, CameraStatus> status = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Running> workers = new(StringComparer.Ordinal);
        private sealed record Running(CameraConfiguration Camera, CancellationTokenSource Stop, Task Task);

        public CameraSupervisor(CameraPipeline pipeline, EventNormalizer normalizer, TimeProvider clock)
            : this(pipeline, (camera, report, token) => new CameraSubscriptionWorker(pipeline, normalizer, clock).Run(camera, report, token)) { }

        // External worker seam for deterministic cancellation/cleanup ordering tests.
        internal CameraSupervisor(CameraPipeline pipeline,
            Func<CameraConfiguration, Action<CameraStatus>, CancellationToken, Task> run)
        {
            this.pipeline = pipeline;
            this.run = run;
        }

        public IReadOnlyList<CameraStatus> Status
        {
            get
            {
                lock (gate)
                {
                    return pipeline.Cameras.Select(c => status.TryGetValue(c.Id, out var s) ? s : new(c.Id,
                        c.Enabled ? "Connecting" : "Disabled")).ToArray();
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var obsolete = workers.Values.Where(w => !pipeline.IsCurrentCamera(w.Camera) || !w.Camera.Enabled).ToArray();
                    // Cancel all first, then join cleanup. Unchanged workers keep polling throughout.
                    foreach (var worker in obsolete)
                    {
                        worker.Stop.Cancel();
                    }
                    await Task.WhenAll(obsolete.Select(w => w.Task));
                    foreach (var worker in obsolete)
                    {
                        workers.Remove(worker.Camera.Id);
                        worker.Stop.Dispose();
                    }
                    var cameras = pipeline.Cameras;
                    lock (gate)
                    {
                        foreach (var id in status.Keys.Where(id => !cameras.Any(c => c.Id == id)).ToArray())
                        {
                            status.Remove(id);
                        }
                        foreach (var c in cameras.Where(c => !c.Enabled))
                        {
                            status[c.Id] = status.TryGetValue(c.Id, out var old) ? old with
                            {
                                State = "Disabled"
                            } : new(c.Id);
                        }
                    }
                    foreach (var camera in cameras.Where(c => c.Enabled && !workers.ContainsKey(c.Id)))
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        // Start asynchronously: a synchronously completing transport cannot hold up other cameras.
                        var task = Task.Run(async () =>
                        {
                            try
                            {
                                await run(camera, s => { lock (gate) { status[camera.Id] = s; } }, stop.Token);
                            }
                            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                            catch (Exception) { lock (gate) { status[camera.Id] = new(camera.Id, "Error"); } }
                        }, CancellationToken.None);
                        workers.Add(camera.Id, new(camera, stop, task));
                    }
                    await pipeline.WaitForChange(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            finally
            {
                foreach (var worker in workers.Values)
                {
                    worker.Stop.Cancel();
                }
                await Task.WhenAll(workers.Values.Select(w => w.Task));
                foreach (var worker in workers.Values)
                {
                    worker.Stop.Dispose();
                }
                workers.Clear();
            }
        }
    }
}
