using OnvifWebhookBridge.Tests.Onvif;

// Test-only entry point. Supply the disposable onvifsim endpoint/control API.
if (args.Length != 2)
{
    Console.Error.WriteLine("Supply simulator device and control URLs.");
    return 2;
}
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    SimulatorPath.VerifySyntheticContent();
    await SimulatorPath.Verify(new Uri(args[0]), new Uri(args[1]), deadline.Token);
    Console.WriteLine("PASS: simulator discovery, subscription, renewal, normalization, history, matching, admission and unsubscribe.");
    return 0;
}
catch (Exception)
{
    // Do not expose network exception messages or credentials in test output.
    Console.Error.WriteLine("FAIL: native simulator verification.");
    return 1;
}
