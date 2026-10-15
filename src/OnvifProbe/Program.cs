using OnvifWebhookBridge.Onvif;
using SharpOnvifClient;
using SharpOnvifClient.Events;
using SharpOnvifCommon.Onvif;
using SharpOnvifCommon.Security;
using SharpOnvifCommon.Soap;
using System.Diagnostics;
using System.Net;
using System.Xml;

// Diagnostic prototype: print every notification; never assume a topic or field schema.
string? endpoint = Environment.GetEnvironmentVariable("ONVIF_ENDPOINT");
string? username = Environment.GetEnvironmentVariable("ONVIF_USERNAME");
string? password = Environment.GetEnvironmentVariable("ONVIF_PASSWORD");
bool raw = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--help" or "-h")
    {
        Console.WriteLine("ONVIF event probe (.NET 10)\n" +
            "  --endpoint URL   (or ONVIF_ENDPOINT; required)\n" +
            "  --username NAME  (or ONVIF_USERNAME; optional)\n" +
            "  --password VALUE (or ONVIF_PASSWORD; prefer environment)\n" +
            "  --raw            Include library-exposed message XML\n" +
            "  --help           Show help\n" +
            "Ctrl+C stops. No notification filtering or deduplication.");
        return 0;
    }
    if (args[i] == "--raw")
    {
        raw = true;
        continue;
    }
    if (args[i] is not ("--endpoint" or "--username" or "--password") || i + 1 >= args.Length)
    {
        Console.Error.WriteLine("Invalid arguments. Use --help.");
        return 2;
    }
    switch (args[i++])
    {
        case "--endpoint":
            endpoint = args[i];
            break;
        case "--username":
            username = args[i];
            break;
        case "--password":
            password = args[i];
            break;
    }
}
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
    uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
{
    Console.Error.WriteLine("Set ONVIF_ENDPOINT or --endpoint to an HTTP(S) device-service URL without embedded credentials.");
    return 2;
}
bool authenticated = !string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password);
if (authenticated && (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)))
{
    Console.Error.WriteLine("Supply both username and password, or neither.");
    return 2;
}

using var stopping = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stopping.Cancel(); };
Console.CancelKeyPress += cancelHandler;
const int requestedLeaseSeconds = 120;
const int pullSeconds = 20;
var authentication = new OnvifAuthenticationSettings(authenticated
    ? DigestAuthentication.HttpDigest | DigestAuthentication.WsUsernameToken
    : DigestAuthentication.None);

try
{
    while (!stopping.IsCancellationRequested)
    {
        using var client = authenticated
            ? new SimpleOnvifClient(endpoint!, username!, password!, authentication)
            : new SimpleOnvifClient(endpoint!);
        string? address = null;
        try
        {
            Status("Connecting to configured endpoint...");
            await WaitForCall(client.GetServicesAsync(), stopping.Token);
            Status("Connected; device services received.");
            var subscription = await WaitForCall(client.PullPointSubscribeAsync(requestedLeaseSeconds), stopping.Token);
            address = subscription.SubscriptionReference?.Address?.Value;
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new InvalidOperationException("Missing subscription address.");
            }

            Status("PullPoint subscription established (no topic filter).");
            if ((subscription.SubscriptionReference?.ReferenceParameters?.Any?.Length ?? 0) > 0)
            {
                Status("Warning: subscription includes reference parameters; SimpleOnvifClient sends only the address. This device may require a lower-level client.");
            }
            // Renew uses the same WS-Notification manager endpoint returned by subscription.
            using var manager = new SubscriptionManagerClient(address, new OnvifClientSettings
            {
                Credentials = authenticated ? new NetworkCredential(username, password) : null,
                Authentication = new AddressedAuthentication(authentication, address),
                Timeout = TimeSpan.FromSeconds(10)
            });
            long leaseStarted = Stopwatch.GetTimestamp();
            double leaseSeconds = LeaseSeconds(subscription.CurrentTime, subscription.TerminationTime);
            while (!stopping.IsCancellationRequested)
            {
                double remaining = leaseSeconds - Stopwatch.GetElapsedTime(leaseStarted).TotalSeconds;
                if (remaining <= pullSeconds + 10)
                {
                    Status("Renewing PullPoint subscription...");
                    var renewed = await manager.RenewAsync(new RenewRequest
                    {
                        TerminationTime = $"PT{requestedLeaseSeconds}S"
                    }, stopping.Token);
                    leaseSeconds = LeaseSeconds(renewed.CurrentTime, renewed.TerminationTime);
                    leaseStarted = Stopwatch.GetTimestamp();
                    Status("Subscription renewed.");
                    remaining = leaseSeconds;
                }
                int timeout = Math.Max(1, Math.Min(pullSeconds, (int)Math.Floor(remaining / 2)));
                var started = Stopwatch.GetTimestamp();
                var batch = await WaitForCall(client.PullPointPullMessagesAsync(address, timeout, 100), stopping.Token);
                var received = DateTimeOffset.Now;
                foreach (var notification in batch.NotificationMessage ?? [])
                {
                    PrintNotification(notification, received, raw);
                }

                if (batch.TerminationTime > batch.CurrentTime)
                {
                    leaseSeconds = LeaseSeconds(batch.CurrentTime, batch.TerminationTime);
                    leaseStarted = Stopwatch.GetTimestamp();
                }
                // Some devices return immediately even with no events. Prevent a busy loop.
                if (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(250))
                {
                    await Task.Delay(250, stopping.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Exception messages can contain endpoint URLs, credentials or server data.
            Status($"Connection/subscription error ({ex.GetType().Name}); retrying in 3 seconds.");
        }
        finally
        {
            if (address is not null)
            {
                try
                {
                    await WaitForCall(client.PullPointUnsubscribeAsync(address), CancellationToken.None, 3);
                    Status("Unsubscribed.");
                }
                catch { Status("Unsubscribe did not complete; the subscription will expire."); }
            }
            // Disposing aborts clients' pending HTTP calls; WaitAsync alone cannot cancel them.
            client.Dispose();
        }
        if (!stopping.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stopping.Token);
        }
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
finally
{
    Console.CancelKeyPress -= cancelHandler;
    Status("Stopped.");
}
return 0;

static void Status(string message) => Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}");

static double LeaseSeconds(DateTime current, DateTime termination)
{
    // Use camera-relative time so local clock drift does not affect the lease calculation.
    double seconds = (termination - current).TotalSeconds;
    if (!double.IsFinite(seconds) || seconds < 2)
    {
        throw new InvalidOperationException("Missing, expired or unusable subscription lease.");
    }

    return seconds;
}

static async Task<T> WaitForCall<T>(Task<T> call, CancellationToken cancellationToken, int timeoutSeconds = 75)
{
    try
    {
        return await call.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
    }
    catch
    {
        // Observe faults if WaitAsync stopped waiting before the underlying call completed.
        _ = call.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        throw;
    }
}

static void PrintNotification(NotificationMessageHolderType notification, DateTimeOffset received, bool raw)
{
    Console.WriteLine($"\n[{received:yyyy-MM-dd HH:mm:ss.fff zzz}] EVENT");
    Console.WriteLine($"  Topic: {string.Concat((notification.Topic?.Any ?? []).Select(n => n.InnerText))}");
    Console.WriteLine($"  Topic dialect: {notification.Topic?.Dialect ?? "(absent)"}");
    foreach (var attribute in notification.Topic?.AnyAttr ?? [])
    {
        Console.WriteLine($"  Topic attribute {attribute.Name} = {attribute.Value}");
    }

    foreach (var node in notification.Topic?.Any ?? [])
    {
        if (node is XmlElement)
        {
            Console.WriteLine($"  Topic element: {node.OuterXml}");
        }
    }

    // References may contain private URLs/tokens. Report presence without leaking values.
    if (notification.SubscriptionReference is not null)
    {
        Console.WriteLine("  Subscription reference: present (address omitted)");
    }

    if (notification.ProducerReference is not null)
    {
        Console.WriteLine("  Producer reference: present (address omitted)");
    }

    var message = notification.Message;
    if (message is null)
    {
        Console.WriteLine("  Message: (absent)");
        return;
    }
    var cameraMessage = message.HasAttribute("UtcTime") ? message
        : message.SelectSingleNode(".//*[@UtcTime]") as XmlElement;
    Console.WriteLine($"  Camera timestamp: {cameraMessage?.GetAttribute("UtcTime") ?? "(absent)"}");
    PrintElement(message, "  ");
    if (raw)
    {
        Console.WriteLine($"  Library-exposed message XML:\n{message.OuterXml}");
    }
}

static void PrintElement(XmlElement element, string indent)
{
    if (element.LocalName == "SimpleItem" && element.HasAttribute("Name") && element.HasAttribute("Value"))
    {
        Console.WriteLine($"{indent}{element.GetAttribute("Name")} = {element.GetAttribute("Value")}");
    }
    else
    {
        Console.WriteLine($"{indent}{element.Name} (namespace: {element.NamespaceURI})");
    }

    foreach (XmlAttribute attribute in element.Attributes)
    {
        Console.WriteLine($"{indent}  @{attribute.Name} = {attribute.Value}");
    }

    foreach (XmlNode child in element.ChildNodes)
    {
        if (child is XmlElement nested)
        {
            PrintElement(nested, indent + "  ");
        }
        else
        {
            Console.WriteLine($"{indent}  {child.NodeType}: {child.Value}");
        }
    }
}
