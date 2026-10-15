using OnvifWebhookBridge.Tests.Onvif;
using OnvifWebhookBridge.Tests.Persistence;

if (args is ["--storage-only"])
{
    try { StoragePath.Verify(); return 0; }
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
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    StoragePath.Verify();
    SimulatorPath.VerifySyntheticContent();
    await SimulatorPath.Verify(new Uri(args[0]), new Uri(args[1]), deadline.Token);
    await ForwardingPath.Verify(new Uri(args[0]), new Uri(args[1]), deadline.Token);
    await PersistencePath.Verify(new Uri(args[0]), new Uri(args[1]), deadline.Token);
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
