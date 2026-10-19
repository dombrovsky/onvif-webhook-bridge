namespace OnvifWebhookBridge.Tests.Web
{
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Rules;
    using OnvifWebhookBridge.Tests.Persistence;
    using System.Net;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;

    // Real local TLS, shared unchanged between JIT and Linux NativeAOT.
    internal static class WebhookTlsPath
    {
        public static async Task Verify(CancellationToken token)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=synthetic.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
            // Reimport for a Windows Schannel-compatible server private key.
            using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
            await using var recorder = builder.Build();
            var count = 0;
            recorder.MapGet("/synthetic", () =>
            {
                Interlocked.Increment(ref count);
                return Results.NoContent();
            });
            await recorder.StartAsync(token);
            var address = SecurityPath.Origin(recorder) + "/synthetic";
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCameraPipeline();
            using var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<CameraPipeline>();
            var queue = provider.GetRequiredService<DispatchQueue>();
            using var dispatcher = new HttpActionDispatcher(queue, pipeline, provider.GetRequiredService<IHttpClientFactory>(), TimeProvider.System);
            using var data = new TemporaryStorage();
            var store = new ConfigurationStore(data.Path, pipeline);
            store.Initialize();
            var match = new ForwardingRule("tls-rule", "tls-camera", "Synthetic/Tls");
            var revision = 0L;
            foreach (var ignore in new[] { false, true, false })
            {
                revision = store.Save(revision, [new("tls-camera", "Synthetic TLS camera", "http://camera.example/device")],
                    [new(match, "Synthetic TLS action", url: StoragePath.Replace(address), ignoreCertificateValidation: ignore)]);
                StoragePath.Check(store.Read().Rules.Single().IgnoreCertificateValidation == ignore, "TlsChoiceRoundTrip");
                StoragePath.Check(pipeline.ManualTest(match.Id), "TlsManualAdmission");
                var job = await queue.TakeAsync(token);
                await dispatcher.Deliver(job, token);
                queue.Complete(job);
                StoragePath.Check(pipeline.LastExecutions.Single().Result ==
                    (ignore ? ExecutionResult.Accepted : ExecutionResult.TransportFailure), "TlsPoolIsolation " + ignore + " " + pipeline.LastExecutions.Single().Result);
            }
            StoragePath.Check(count == 1, "OnlyOptedInRequestArrived");
            StoragePath.Check(pipeline.ManualTest(match.Id), "TlsQueuedBeforeEdit");
            var stale = await queue.TakeAsync(token);
            store.Save(revision, [new("tls-camera", "Synthetic TLS camera", "http://camera.example/device")],
                [new(match, "Synthetic TLS action", ignoreCertificateValidation: true)]);
            StoragePath.Check(!pipeline.Eligible(stale), "TlsEditInvalidatesQueuedJob");
            await dispatcher.Deliver(stale, token);
            queue.Complete(stale);
            StoragePath.Check(count == 1 && pipeline.LastExecutions.Single().Result == ExecutionResult.Stale, "TlsStaleNeverSends");
            using var cameraHandler = HttpActionDispatcher.CreateHandler();
            using var cameraClient = new HttpClient(cameraHandler);
            try
            {
                using var response = await cameraClient.GetAsync(address, token);
                throw new InvalidOperationException("Camera TLS must remain strict.");
            }
            catch (HttpRequestException)
            {
            }
            await recorder.StopAsync(token);
            Console.WriteLine("PASS: per-webhook TLS opt-in, strict pool isolation, persistence, stale jobs and strict camera transport.");
        }
    }
}
