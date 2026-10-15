namespace OnvifWebhookBridge.Dispatch
{
    using System.Net;
    using System.Net.Sockets;
    using System.Text;

    public sealed class HttpActionDispatcher(DispatchQueue queue, CameraPipeline configuration,
        IHttpClientFactory clients, TimeProvider clock, ILogger<HttpActionDispatcher>? logger = null) : BackgroundService
    {
        public const string ClientName = "actions";
        public static readonly TimeSpan JobDeadline = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Consume(stoppingToken))); }
            finally
            {
                foreach (var job in queue.Stop()) { Record(job, ExecutionResult.Cancelled, 0, null, clock.GetTimestamp()); }
            }
        }
        private async Task Consume(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    var job = await queue.TakeAsync(token);
                    try { await Deliver(job, token); }
                    finally { queue.Complete(job); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        internal async Task Deliver(DispatchJob job, CancellationToken stopping)
        {
            var started = clock.GetTimestamp();
            var attempts = 0;
            int? code = null;
            var result = ExecutionResult.TransportFailure;
            var remaining = JobDeadline - clock.GetElapsedTime(job.EnqueuedAt);
            if (remaining <= TimeSpan.Zero) { Record(job, ExecutionResult.Expired, 0, null, started); return; }
            using var deadline = new CancellationTokenSource(remaining, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, job.Cancellation, deadline.Token);
            try
            {
                using var client = clients.CreateClient(ClientName);
                for (var attempt = 0; attempt <= job.Action.AdditionalAttempts; attempt++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!configuration.Eligible(job)) { result = ExecutionResult.Stale; break; }
                    Record(job, ExecutionResult.Running, attempts, code, started);
                    using var timeout = new CancellationTokenSource(AttemptTimeout, clock);
                    using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);
                    using var request = new HttpRequestMessage(new HttpMethod(job.Action.Method), job.Action.Address);
                    foreach (var header in job.Action.Headers) { request.Headers.Add(header.Name, header.Value); }
                    if (job.Action.JsonBody is { } json) { request.Content = new StringContent(json, Encoding.UTF8, "application/json"); }
                    var retry = false;
                    TimeSpan? retryAfter = null;
                    code = null;
                    attempts++;
                    try
                    {
                        if (!configuration.Eligible(job)) { result = ExecutionResult.Stale; attempts--; break; }
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptToken.Token);
                        code = (int)response.StatusCode;
                        result = response.IsSuccessStatusCode ? ExecutionResult.Accepted : ExecutionResult.HttpFailure;
                        retry = code is 408 or 429 || code >= 500;
                        retryAfter = response.Headers.RetryAfter?.Delta;
                        if (response.Headers.RetryAfter?.Date is { } date) { retryAfter = date - clock.GetUtcNow(); }
                        // Response bodies are never read, retained or logged.
                    }
                    catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                    { result = ExecutionResult.Timeout; retry = true; }
                    catch (HttpRequestException) { result = ExecutionResult.TransportFailure; retry = true; }
                    catch (IOException) { result = ExecutionResult.TransportFailure; retry = true; }
                    if (!retry || attempt == job.Action.AdditionalAttempts) { break; }
                    var delay = retryAfter ?? TimeSpan.FromMilliseconds(200 * (attempt + 1) + Random.Shared.Next(100));
                    if (delay < TimeSpan.Zero) { delay = TimeSpan.Zero; }
                    remaining = JobDeadline - clock.GetElapsedTime(job.EnqueuedAt);
                    // Do not retry earlier than a server asks or beyond the total deadline.
                    if (delay >= remaining) { result = ExecutionResult.Expired; break; }
                    await Task.Delay(delay, clock, linked.Token);
                }
            }
            catch (OperationCanceledException)
            { result = stopping.IsCancellationRequested || job.Cancellation.IsCancellationRequested ? ExecutionResult.Cancelled : ExecutionResult.Expired; }
            catch (Exception)
            {
                // Fixed category only, including malformed response/header/transport exceptions.
                result = ExecutionResult.TransportFailure;
            }
            Record(job, result, attempts, code, started);
        }
        private void Record(DispatchJob job, ExecutionResult result, int attempts, int? code, long started)
        {
            var duration = clock.GetElapsedTime(started);
            configuration.Record(new(job.Id, job.RuleId, clock.GetUtcNow(), result, attempts, code, duration));
            if (result != ExecutionResult.Running)
            {
                logger?.LogInformation("HTTP job {JobId}: {Result}; attempts {Attempts}; status {Status}; duration ms {Duration}",
                    job.Id, result, attempts, code, duration.TotalMilliseconds);
            }
        }

        internal static SocketsHttpHandler CreateHandler() => new()
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            MaxResponseHeadersLength = 32, ConnectTimeout = AttemptTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (HttpAction.ForbiddenHost(context.DnsEndPoint.Host) || addresses.Length == 0 || addresses.Any(HttpAction.ForbiddenAddress))
                { throw new HttpRequestException("Blocked destination."); }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
    }
}
