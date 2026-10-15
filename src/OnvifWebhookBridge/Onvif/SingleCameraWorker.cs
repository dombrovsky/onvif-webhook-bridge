namespace OnvifWebhookBridge.Onvif
{
    using System.Diagnostics;
    using System.Net;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using SharpOnvifClient;
    using SharpOnvifClient.Events;
    using SharpOnvifCommon.Security;
    using SharpOnvifCommon.Soap;

    // Single startup snapshot only. Runtime worker reconciliation/supervision belongs to phase 4.
    public sealed class SingleCameraWorker(SingleCameraPipeline pipeline, EventNormalizer normalizer) : BackgroundService
    {
        public string State { get; private set; } = "Disabled";

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var camera = pipeline.Camera;
            if (camera is not { Enabled: true }) { return; }
            var failures = 0;
            try
            {
                while (!stoppingToken.IsCancellationRequested && pipeline.IsCurrentCamera(camera))
                {
                    var authentication = new OnvifAuthenticationSettings(string.IsNullOrEmpty(camera.Username)
                        ? DigestAuthentication.None : DigestAuthentication.HttpDigest | DigestAuthentication.WsUsernameToken);
                    using var client = new BoundedClient(camera, authentication);
                    // Convenience APIs have no cancellation parameter: dispose aborts their transport.
                    using var abort = stoppingToken.Register(client.Dispose);
                    string? address = null;
                    try
                    {
                        State = "Connecting";
                        await Call(client.GetServicesAsync(), client, stoppingToken, 10);
                        var subscription = await Call(client.PullPointSubscribeAsync(120), client, stoppingToken, 10);
                        address = subscription.SubscriptionReference?.Address?.Value;
                        if (string.IsNullOrEmpty(address) || (subscription.SubscriptionReference?.ReferenceParameters?.Any?.Length ?? 0) > 0)
                        { throw new InvalidOperationException("Unsupported subscription reference."); }
                        using var manager = new SubscriptionManagerClient(address, new OnvifClientSettings
                        {
                            Credentials = string.IsNullOrEmpty(camera.Username) ? null : new NetworkCredential(camera.Username, camera.Password),
                            Authentication = new AddressedAuthentication(authentication, address), Timeout = TimeSpan.FromSeconds(10),
                            MaxResponseContentBytes = 1024 * 1024
                        });
                        var lease = Lease(subscription.CurrentTime, subscription.TerminationTime);
                        var leaseStarted = Stopwatch.GetTimestamp();
                        pipeline.ResetEpoch(); State = "Subscribed"; failures = 0;
                        while (!stoppingToken.IsCancellationRequested && pipeline.IsCurrentCamera(camera))
                        {
                            var remaining = lease - Stopwatch.GetElapsedTime(leaseStarted).TotalSeconds;
                            if (remaining <= 30)
                            {
                                using var renewalDeadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                renewalDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                                var renewed = await manager.RenewAsync(new RenewRequest { TerminationTime = "PT120S" }, renewalDeadline.Token);
                                lease = Lease(renewed.CurrentTime, renewed.TerminationTime); leaseStarted = Stopwatch.GetTimestamp(); remaining = lease;
                            }
                            var timeout = Math.Max(1, Math.Min(20, (int)Math.Floor(remaining / 2)));
                            var started = Stopwatch.GetTimestamp();
                            var batch = await Call(client.PullPointPullMessagesAsync(address, timeout, 100), client, stoppingToken, timeout + 10);
                            foreach (var notification in batch.NotificationMessage ?? [])
                            { pipeline.Process(normalizer.Normalize(notification, camera.Id, camera.Name, DateTimeOffset.UtcNow), camera); }
                            if (batch.TerminationTime > batch.CurrentTime)
                            { lease = Lease(batch.CurrentTime, batch.TerminationTime); leaseStarted = Stopwatch.GetTimestamp(); }
                            if (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(250)) { await Task.Delay(250, stoppingToken); }
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception) { State = "Reconnecting"; }
                    finally
                    {
                        if (address is not null)
                        {
                            // A cancelled/disposed polling transport cannot perform shutdown cleanup.
                            using var cleanup = new BoundedClient(camera, authentication);
                            try { await Call(cleanup.PullPointUnsubscribeAsync(address), cleanup, CancellationToken.None, 3); } catch { }
                        }
                        client.Dispose();
                    }
                    if (!stoppingToken.IsCancellationRequested)
                    { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(30000, 1000 * Math.Pow(2, Math.Min(failures++, 5))) + Random.Shared.Next(250)), stoppingToken); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            finally { State = "Stopped"; }
        }

        // 0.11.2's protected settings are copied to generated clients, including pull clients.
        private sealed class BoundedClient : SimpleOnvifClient
        {
            public BoundedClient(CameraConfiguration camera, OnvifAuthenticationSettings authentication)
                : base(camera.Endpoint, camera.Username!, camera.Password!, authentication)
            { _settings.MaxResponseContentBytes = 1024 * 1024; _settings.Timeout = TimeSpan.FromSeconds(10); }
        }

        private static double Lease(DateTime current, DateTime termination) => (termination - current).TotalSeconds is var seconds && seconds >= 2
            ? seconds : throw new InvalidOperationException("Unusable lease.");
        private static async Task<T> Call<T>(Task<T> operation, IDisposable client, CancellationToken token, int seconds)
        {
            try { return await operation.WaitAsync(TimeSpan.FromSeconds(seconds), token); }
            catch
            {
                client.Dispose();
                _ = operation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
        }
    }
}
