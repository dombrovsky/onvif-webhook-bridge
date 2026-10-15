namespace OnvifWebhookBridge.Onvif
{
    using System.Net;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using SharpOnvifClient;
    using SharpOnvifClient.Events;
    using SharpOnvifCommon.Security;
    using SharpOnvifCommon.Soap;

    // One sequential session per supervisor entry. Reuses the phase 3 path and shared renewal adapter.
    public sealed class CameraSubscriptionWorker(CameraPipeline pipeline, EventNormalizer normalizer, TimeProvider clock)
    {
        public async Task Run(CameraConfiguration camera, Action<CameraStatus> report, CancellationToken token)
        {
            var status = new CameraStatus(camera.Id, "Connecting");
            string? pendingCleanup = null;
            void State(string state) { status = status with { State = state }; report(status); }
            try
            {
                while (!token.IsCancellationRequested && pipeline.IsCurrentCamera(camera))
                {
                    var authentication = new OnvifAuthenticationSettings(string.IsNullOrEmpty(camera.Username)
                        ? DigestAuthentication.None : DigestAuthentication.HttpDigest | DigestAuthentication.WsUsernameToken);
                    using var client = new BoundedClient(camera, authentication);
                    // Convenience APIs have no cancellation parameter: dispose aborts their transport.
                    using var abort = token.Register(client.Dispose);
                    string? address = null;
                    try
                    {
                        State("Connecting");
                        await Call(client.GetServicesAsync(), client, token, 10);
                        status = status with { LastContact = clock.GetUtcNow() }; report(status);
                        if (pendingCleanup is not null)
                        {
                            // A peer may have been offline throughout the previous bounded cleanup.
                            // Retry its last known address once after discovery, before creating another lease.
                            using var retryCleanup = new BoundedClient(camera, authentication);
                            try { await Call(retryCleanup.PullPointUnsubscribeAsync(pendingCleanup), retryCleanup, token, 3); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch (Exception) { } // Already expired/removed or still unavailable: best effort only.
                            pendingCleanup = null;
                        }
                        var began = clock.GetTimestamp();
                        var subscription = await Call(client.PullPointSubscribeAsync(120), client, token, 10);
                        address = subscription.SubscriptionReference?.Address?.Value;
                        if (string.IsNullOrEmpty(address) || (subscription.SubscriptionReference?.ReferenceParameters?.Any?.Length ?? 0) > 0)
                        { throw new InvalidOperationException("Unsupported subscription reference."); }
                        using var manager = new SubscriptionManagerClient(address, new OnvifClientSettings
                        {
                            Credentials = string.IsNullOrEmpty(camera.Username) ? null : new NetworkCredential(camera.Username, camera.Password),
                            Authentication = new AddressedAuthentication(authentication, address), Timeout = TimeSpan.FromSeconds(10),
                            MaxResponseContentBytes = 1024 * 1024
                        });
                        var lease = SubscriptionTiming.Lease(subscription.CurrentTime, subscription.TerminationTime);
                        var leaseStarted = began;
                        var sessionStarted = began;
                        pipeline.ResetEpoch(camera);
                        status = status with { Epoch = status.Epoch + 1, LastContact = clock.GetUtcNow() }; State("Subscribed");
                        while (!token.IsCancellationRequested && pipeline.IsCurrentCamera(camera))
                        {
                            var remaining = lease - clock.GetElapsedTime(leaseStarted).TotalSeconds;
                            if (remaining <= 0) { throw new InvalidOperationException("Expired lease."); }
                            if (SubscriptionTiming.Renew(remaining))
                            {
                                began = clock.GetTimestamp();
                                using var renewalDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                                renewalDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(10, remaining)));
                                var renewed = await manager.RenewAsync(new RenewRequest { TerminationTime = "PT120S" }, renewalDeadline.Token);
                                lease = SubscriptionTiming.Lease(renewed.CurrentTime, renewed.TerminationTime);
                                leaseStarted = began;
                                remaining = lease - clock.GetElapsedTime(leaseStarted).TotalSeconds;
                                status = status with { LastContact = clock.GetUtcNow() }; report(status);
                                // A short lease cannot accommodate a long poll with transport headroom.
                                if (remaining <= 20) { throw new InvalidOperationException("Unusable renewed lease."); }
                            }
                            var timeout = SubscriptionTiming.PollSeconds(remaining);
                            began = clock.GetTimestamp();
                            var batch = await Call(client.PullPointPullMessagesAsync(address, timeout, 100), client, token,
                                Math.Min(timeout + 10, remaining));
                            status = status with { LastContact = clock.GetUtcNow() };
                            foreach (var notification in batch.NotificationMessage ?? [])
                            {
                                var received = clock.GetUtcNow();
                                pipeline.Process(normalizer.Normalize(notification, camera.Id, camera.Name, received), camera);
                                status = status with { LastEvent = received };
                            }
                            // Empty responses still establish successful contact. Repeated early faults must retain backoff.
                            if (clock.GetElapsedTime(sessionStarted) >= TimeSpan.FromSeconds(30)) { status = status with { Failures = 0 }; }
                            report(status);
                            // Validate returned timing even when expired; never silently keep the old lease.
                            lease = SubscriptionTiming.Lease(batch.CurrentTime, batch.TerminationTime);
                            leaseStarted = began;
                            if (clock.GetElapsedTime(began) < TimeSpan.FromMilliseconds(250))
                            { await Task.Delay(TimeSpan.FromMilliseconds(250), clock, token); }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception)
                    {
                        status = status with { Failures = Math.Min(status.Failures + 1, 32) };
                        State(status.Failures >= 3 ? "Error" : "Reconnecting");
                    }
                    finally
                    {
                        client.Dispose();
                        if (address is not null)
                        {
                            // Independent transport: cancellation has already disposed the polling client.
                            using var cleanup = new BoundedClient(camera, authentication);
                            try { await Call(cleanup.PullPointUnsubscribeAsync(address), cleanup, CancellationToken.None, 3); }
                            catch { pendingCleanup = address; }
                        }
                    }
                    if (!token.IsCancellationRequested && pipeline.IsCurrentCamera(camera))
                    { await Task.Delay(SubscriptionTiming.Backoff(status.Failures, Random.Shared.NextDouble()), clock, token); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { State("Stopped"); }
        }

        // Pinned 0.11.2 protected settings are copied to generated clients, including pull clients.
        private sealed class BoundedClient : SimpleOnvifClient
        {
            public BoundedClient(CameraConfiguration camera, OnvifAuthenticationSettings authentication)
                : base(camera.Endpoint, camera.Username!, camera.Password!, authentication)
            { _settings.MaxResponseContentBytes = 1024 * 1024; _settings.Timeout = TimeSpan.FromSeconds(10); }
        }

        private static async Task<T> Call<T>(Task<T> operation, IDisposable client, CancellationToken token, double seconds)
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
