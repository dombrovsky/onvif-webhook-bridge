# Linux x64 NativeAOT protocol verification using only disposable synthetic onvifsim.
# Run from the repository root. Docker engine/image access is required.
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path '.').Path
$stage = Join-Path $repository ".local/phase4-native-source-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $stage | Out-Null
# Explicit source allowlist: no private settings, captures, package cache or build output.
$files = @('Directory.Build.props', 'Directory.Build.targets',
    'src/OnvifWebhookBridge/OnvifWebhookBridge.csproj', 'src/OnvifWebhookBridge/Program.cs',
    'tests/OnvifWebhookBridge.NativeSmoke/OnvifWebhookBridge.NativeSmoke.csproj',
    'tests/OnvifWebhookBridge.NativeSmoke/Program.cs',
    'tests/OnvifWebhookBridge.Tests/Onvif/SimulatorPath.cs',
    'tests/OnvifWebhookBridge.Tests/Onvif/ForwardingPath.cs',
    'tests/OnvifWebhookBridge.Tests/Onvif/LifecyclePath.cs')
foreach ($folder in @('Events', 'Rules', 'Onvif', 'Dispatch')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $repository "src/OnvifWebhookBridge/$folder") -Filter '*.cs' |
        ForEach-Object { "src/OnvifWebhookBridge/$folder/$($_.Name)" }
}
foreach ($file in $files) {
    $target = Join-Path $stage $file
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $file) -Destination $target
}
@'
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /source
COPY . .
RUN dotnet publish tests/OnvifWebhookBridge.NativeSmoke/OnvifWebhookBridge.NativeSmoke.csproj -c Release -r linux-x64 -o /app/publish
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:da5846024d0008b48259b53a8953d39381fe5b7ec99ef1449ba4ba927f201490
WORKDIR /app
COPY --from=build /app/publish/ ./
USER $APP_UID
ENTRYPOINT ["./OnvifWebhookBridge.NativeSmoke"]
'@ | Set-Content -LiteralPath (Join-Path $stage 'Dockerfile')
docker build --platform linux/amd64 --tag onvif-bridge-tests/phase4-native $stage
if ($LASTEXITCODE -ne 0) { throw 'Native test image build failed.' }
$revision = '8f86706c2ba768006e85b38f6bc1c760d36f1bba'
$bind = [Net.IPAddress]::Any.ToString()
$simulator = docker run --detach --rm "onvif-bridge-tests/onvifsim:$revision" --cameras 1 --bind $bind --control-bind $bind --no-discovery
if ($LASTEXITCODE -ne 0) { throw 'Simulator startup failed.' }
try {
    $device = [UriBuilder]::new('http', [Net.IPAddress]::Loopback.ToString(), 8000, '/onvif/device_service').Uri.AbsoluteUri
    $control = [UriBuilder]::new('http', [Net.IPAddress]::Loopback.ToString(), 9000).Uri.AbsoluteUri
    $second = [UriBuilder]::new('http', [Net.IPAddress]::Loopback.ToString(), 8001, '/onvif/device_service').Uri.AbsoluteUri
    docker run --rm --network "container:$simulator" onvif-bridge-tests/phase4-native $device $control $second
    if ($LASTEXITCODE -ne 0) { throw 'Native simulator verification failed.' }
}
finally {
    docker stop $simulator | Out-Null
}
