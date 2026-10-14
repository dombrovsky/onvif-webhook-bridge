# Synthetic localhost SOAP service only; never reads a personal launch profile.
# Run from the repository root after a Debug build, using PowerShell 7.
$ErrorActionPreference = 'Stop'
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add('http://localhost:18765/')
$listener.Start()
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.ArgumentList.Add((Join-Path $PWD 'tools/OnvifProbe/bin/Debug/net10.0/OnvifProbe.dll'))
$start.ArgumentList.Add('--endpoint')
$start.ArgumentList.Add('http://localhost:18765/device')
$start.ArgumentList.Add('--raw')
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.UseShellExecute = $false
$start.Environment.Remove('ONVIF_USERNAME') | Out-Null
$start.Environment.Remove('ONVIF_PASSWORD') | Out-Null
$probe = [Diagnostics.Process]::Start($start)
$outputTask = $probe.StandardOutput.ReadToEndAsync()
$errorTask = $probe.StandardError.ReadToEndAsync()
$subscriptions = 0
$pulls = 0
$renewals = 0
$faultSent = $false
$messageIds = [Collections.Generic.HashSet[string]]::new()
try {
    while ($pulls -lt 2) {
        $pending = $listener.GetContextAsync()
        if (!$pending.Wait(15000)) { throw 'Mock request timed out.' }
        $context = $pending.Result
        $reader = [IO.StreamReader]::new($context.Request.InputStream)
        $request = $reader.ReadToEnd()
        $reader.Dispose()
        [xml]$requestXml = $request
        $operation = $requestXml.DocumentElement.ChildNodes | Where-Object LocalName -EQ 'Body' | ForEach-Object { $_.FirstChild.LocalName }
        $now = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $end = [DateTime]::UtcNow.AddSeconds(25).ToString('yyyy-MM-ddTHH:mm:ssZ')
        switch ($operation) {
            'GetServices' {
                $body = '<tds:GetServicesResponse><tds:Service><tds:Namespace>http://www.onvif.org/ver10/events/wsdl</tds:Namespace><tds:XAddr>http://localhost:18765/events</tds:XAddr><tds:Version><tt:Major>2</tt:Major><tt:Minor>0</tt:Minor></tds:Version></tds:Service></tds:GetServicesResponse>'
            }
            'CreatePullPointSubscription' {
                if ($request -match '<[^>]*Filter[ >]') { throw 'Unexpected subscription filter.' }
                $subscriptions++
                $body = "<tev:CreatePullPointSubscriptionResponse><tev:SubscriptionReference><wsa:Address>http://localhost:18765/pull</wsa:Address></tev:SubscriptionReference><wsnt:CurrentTime>$now</wsnt:CurrentTime><wsnt:TerminationTime>$end</wsnt:TerminationTime></tev:CreatePullPointSubscriptionResponse>"
            }
            'Renew' {
                $header = $requestXml.DocumentElement.ChildNodes | Where-Object LocalName -EQ 'Header'
                $addressing = 'http://www.w3.org/2005/08/addressing'
                $messageId = $header.GetElementsByTagName('MessageID', $addressing)
                if ($messageId.Count -ne 1 -or $messageId[0].InnerText -notmatch '^urn:uuid:' -or !$messageIds.Add($messageId[0].InnerText)) {
                    throw 'Renew requires a unique WS-Addressing MessageID.'
                }
                $action = $header.GetElementsByTagName('Action', $addressing)
                $to = $header.GetElementsByTagName('To', $addressing)
                $replyTo = $header.GetElementsByTagName('ReplyTo', $addressing)
                if ($action.Count -ne 1 -or $action[0].InnerText -notlike '*RenewRequest' -or
                    $to.Count -ne 1 -or $to[0].InnerText -ne 'http://localhost:18765/pull' -or
                    $replyTo.Count -ne 1 -or $replyTo[0].InnerText -ne ($addressing + '/anonymous')) {
                    throw 'Incomplete WS-Addressing renewal headers.'
                }
                $renewals++
                $end = [DateTime]::UtcNow.AddSeconds(120).ToString('yyyy-MM-ddTHH:mm:ssZ')
                $body = "<wsnt:RenewResponse><wsnt:TerminationTime>$end</wsnt:TerminationTime><wsnt:CurrentTime>$now</wsnt:CurrentTime></wsnt:RenewResponse>"
            }
            'PullMessages' {
                if ($pulls -eq 1 -and !$faultSent) {
                    $faultSent = $true
                    $context.Response.StatusCode = 500
                    $body = '<s:Fault><s:Code><s:Value>s:Sender</s:Value></s:Code><s:Reason><s:Text xml:lang="en">Simulated expired subscription</s:Text></s:Reason></s:Fault>'
                } else {
                    $pulls++
                    $end = [DateTime]::UtcNow.AddSeconds(120).ToString('yyyy-MM-ddTHH:mm:ssZ')
                    $notifications = ''
                    foreach ($value in @('true','true','false')) {
                        $notifications += "<wsnt:NotificationMessage><wsnt:Topic Dialect='urn:synthetic:topic'>Synthetic/Detection</wsnt:Topic><wsnt:Message><tt:Message UtcTime='$now' PropertyOperation='Changed'><tt:Source><tt:SimpleItem Name='Input' Value='example'/><tt:SimpleItem Name='Input' Value='duplicate'/></tt:Source><tt:Key><tt:SimpleItem Name='Key' Value='sample'/></tt:Key><tt:Data><tt:SimpleItem Name='Detected' Value='$value'/><tt:ElementItem Name='Shape'><extra:Shape custom='kept'>structured-value</extra:Shape></tt:ElementItem></tt:Data><extra:Unknown flag='preserved'>unknown-value</extra:Unknown></tt:Message></wsnt:Message></wsnt:NotificationMessage>"
                    }
                    $body = "<tev:PullMessagesResponse><tev:CurrentTime>$now</tev:CurrentTime><tev:TerminationTime>$end</tev:TerminationTime>$notifications</tev:PullMessagesResponse>"
                }
            }
            'Unsubscribe' { $body = '<wsnt:UnsubscribeResponse/>' }
            default { throw "Unexpected operation: $operation" }
        }
        $soap = "<s:Envelope xmlns:s='http://www.w3.org/2003/05/soap-envelope' xmlns:tds='http://www.onvif.org/ver10/device/wsdl' xmlns:tev='http://www.onvif.org/ver10/events/wsdl' xmlns:tt='http://www.onvif.org/ver10/schema' xmlns:wsa='http://www.w3.org/2005/08/addressing' xmlns:wsnt='http://docs.oasis-open.org/wsn/b-2' xmlns:extra='urn:synthetic:extension'><s:Body>$body</s:Body></s:Envelope>"
        $bytes = [Text.Encoding]::UTF8.GetBytes($soap)
        $context.Response.ContentType = 'application/soap+xml; charset=utf-8'
        $context.Response.ContentLength64 = $bytes.Length
        $context.Response.OutputStream.Write($bytes)
        $context.Response.Close()
    }
    Start-Sleep -Milliseconds 500
} finally {
    if (!$probe.HasExited) { $probe.Kill($true) }
    $probe.WaitForExit()
    $listener.Stop()
    $listener.Close()
}
$output = $outputTask.Result
$errors = $errorTask.Result
if ($errors) { throw $errors }
foreach ($expected in @('Topic: Synthetic/Detection','Detected = true','Detected = false','Input = duplicate','structured-value','unknown-value','@UtcTime','@PropertyOperation','Library-exposed message XML','Subscription renewed.','retrying in 3 seconds.')) {
    if (!$output.Contains($expected)) { throw "Missing output: $expected`n$output" }
}
$eventCount = [regex]::Matches($output, '\] EVENT').Count
if ($eventCount -ne 6 -or $subscriptions -ne 2 -or $renewals -lt 2) { throw "Unexpected counts: $eventCount events, $subscriptions subscriptions, $renewals renewals." }
Write-Output "PASS: simulated SOAP service; $eventCount events including repeats/false, duplicates/extensions/raw XML, unique WS-Addressing renewal headers and fault-driven resubscription."
Write-Output 'Child process terminated by harness; Ctrl+C was not tested by this harness.'
