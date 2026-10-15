# Test-only native browser walkthrough. Requires the local phase7-native image,
# installed Edge (or ONVIF_BROWSER_CHANNEL), Node and an external Playwright installation.
param([Parameter(Mandatory)][string]$PlaywrightModule)
$ErrorActionPreference = 'Stop'
$revision = '8f86706c2ba768006e85b38f6bc1c760d36f1bba'
$bind = [Net.IPAddress]::Any.ToString()
$fixture = $null
$loopback = [Net.IPAddress]::Loopback.ToString()
$simulator = docker run --detach --rm --publish "${loopback}::8080" --publish "${loopback}::9000" "onvif-bridge-tests/onvifsim:$revision" --cameras 1 --bind $bind --control-bind $bind --no-discovery
if ($LASTEXITCODE -ne 0) { throw 'Simulator start failed.' }
try {
    $fixture = docker run --detach --network "container:$simulator" --env ONVIF_BROWSER_DATA=/tmp/browser-data --env ONVIF_BROWSER_PORT=8080 onvif-bridge-tests/phase7-native --browser-fixture
    if ($LASTEXITCODE -ne 0) { throw 'Browser fixture start failed.' }
    $mapping = docker port $simulator 8080/tcp
    $port = [int](($mapping | Select-Object -First 1) -split ':')[-1]
    $env:ONVIF_BROWSER_ORIGIN = [UriBuilder]::new('http', [Net.IPAddress]::Loopback.ToString(), $port).Uri.GetLeftPart([UriPartial]::Authority)
    $controlPort = [int]((docker port $simulator 9000/tcp | Select-Object -First 1) -split ':')[-1]
    $env:ONVIF_BROWSER_CONTROL = [UriBuilder]::new('http', [Net.IPAddress]::Loopback.ToString(), $controlPort).Uri.GetLeftPart([UriPartial]::Authority)
    $env:ONVIF_BROWSER_CAMERA = [UriBuilder]::new('http', [Net.IPAddress]::Loopback.ToString(), 8000, '/onvif/device_service').Uri.AbsoluteUri
    $env:ONVIF_BROWSER_CONTAINER = $fixture
    $env:ONVIF_PLAYWRIGHT_MODULE = $PlaywrightModule
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        try { $ready = (Invoke-WebRequest "$env:ONVIF_BROWSER_ORIGIN/health" -TimeoutSec 2).StatusCode -eq 200 } catch { }
        if ($ready) { break }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw 'Browser fixture readiness failed.' }
    node tests/BrowserWalkthrough.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Browser walkthrough failed.' }
}
finally {
    if ($fixture) { docker rm --force $fixture | Out-Null }
    docker stop $simulator | Out-Null
    foreach ($name in @('ONVIF_BROWSER_ORIGIN','ONVIF_BROWSER_CONTROL','ONVIF_BROWSER_CAMERA','ONVIF_BROWSER_CONTAINER','ONVIF_PLAYWRIGHT_MODULE')) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
}
