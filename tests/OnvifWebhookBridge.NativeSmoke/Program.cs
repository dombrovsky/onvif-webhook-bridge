using OnvifWebhookBridge.Tests.Onvif;
using OnvifWebhookBridge.Tests.Persistence;
using OnvifWebhookBridge.Tests.Web;

if (args is ["--recorder"])
{
    await BrowserFixture.RunRecorder();
    return 0;
}

if (args is ["--browser-fixture"])
{
    await BrowserFixture.Run();
    return 0;
}

if (args is ["--storage-only"])
{
    try
    {
        StoragePath.Verify();
        await AuthenticationPath.Verify();
        await ExecutablePath.Verify(CancellationToken.None);
        await SecurityPath.Verify(CancellationToken.None);
        await HealthPath.Verify(CancellationToken.None);
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine("FAIL: native storage verification (" + error.GetType().Name + ")."); return 1; }
}

// Test-only entry point. Supply the disposable onvifsim endpoint/control API.
if (args.Length != 3)
{
    Console.Error.WriteLine("Supply simulator device and control URLs.");
    return 2;
}
try
{
    // Emulation makes the unchanged 600,000-iteration password checks expensive.
    // This is only the suite's overall watchdog; operation/cleanup deadlines stay unchanged.
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(
        Environment.GetEnvironmentVariable("ONVIF_TEST_EMULATION") == "1" ? 30 : 5));
    StoragePath.Verify();
    await AuthenticationPath.Verify();
    await ExecutablePath.Verify(deadline.Token);
    await SecurityPath.Verify(deadline.Token);
    await HealthPath.Verify(deadline.Token);
    SimulatorPath.VerifySyntheticContent();
    await SimulatorPath.Verify(new Uri(args[0]), new Uri(args[1]), deadline.Token);
    await ForwardingPath.Verify(new Uri(args[0]), new Uri(args[1]), deadline.Token);
    await PersistencePath.Verify(new Uri(args[0]), new Uri(args[1]), deadline.Token);
    await ApiPath.Verify(new Uri(args[0]), new Uri(args[1]), deadline.Token);
    await LifecyclePath.Verify(new Uri(args[0]), new Uri(args[2]), new Uri(args[1]), deadline.Token);
    Console.WriteLine("PASS: simulator subscription/renewal, matching/history and HTTP forwarding and multiple-camera lifecycle/reconciliation, faults, lease renewal, recovery and cleanup.");
    return 0;
}
catch (Exception)
{
    // Do not expose network exception messages or credentials in test output.
    Console.Error.WriteLine("FAIL: native simulator verification.");
    return 1;
}
