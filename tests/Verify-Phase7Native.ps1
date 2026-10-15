# Linux amd64/arm64 NativeAOT protocol verification using only disposable synthetic onvifsim.
# Run from the repository root. Docker engine/image access is required.
param([switch]$StorageOnly, [ValidateSet("linux/amd64", "linux/arm64")][string]$Platform = "linux/amd64", [switch]$CrossCompile)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path '.').Path
$stage = Join-Path $repository ".local/phase7-native-source-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $stage | Out-Null
# Explicit source allowlist: no private settings, captures, package cache or build output.
$files = @('Directory.Build.props', 'Directory.Build.targets',
    'src/OnvifWebhookBridge/OnvifWebhookBridge.csproj', 'src/OnvifWebhookBridge/Program.cs',
    'tests/OnvifWebhookBridge.NativeSmoke/OnvifWebhookBridge.NativeSmoke.csproj',
    'tests/OnvifWebhookBridge.NativeSmoke/Program.cs',
    'tests/OnvifWebhookBridge.Tests/Onvif/SimulatorPath.cs',
    'tests/OnvifWebhookBridge.Tests/Onvif/ForwardingPath.cs',
    'tests/OnvifWebhookBridge.Tests/Onvif/LifecyclePath.cs',
    'tests/OnvifWebhookBridge.Tests/Onvif/PersistencePath.cs',
    'tests/OnvifWebhookBridge.Tests/Persistence/StoragePath.cs', 'tests/OnvifWebhookBridge.Tests/Web/SecurityPath.cs', 'tests/OnvifWebhookBridge.Tests/Web/ApiPath.cs', 'tests/OnvifWebhookBridge.Tests/Web/AuthenticationPath.cs', 'tests/OnvifWebhookBridge.Tests/Web/ExecutablePath.cs')
$files += @('tests/OnvifWebhookBridge.Tests/Web/BrowserPath.cs', 'tests/OnvifWebhookBridge.Tests/Web/BrowserFixture.cs')
$files += Get-ChildItem -LiteralPath (Join-Path $repository 'src/OnvifWebhookBridge/wwwroot') -File | ForEach-Object { "src/OnvifWebhookBridge/wwwroot/$($_.Name)" }
foreach ($folder in @('Events', 'Rules', 'Onvif', 'Dispatch', 'Persistence', 'Web')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $repository "src/OnvifWebhookBridge/$folder") -Filter '*.cs' |
        ForEach-Object { "src/OnvifWebhookBridge/$folder/$($_.Name)" }
}
$files += Get-ChildItem -LiteralPath (Join-Path $repository "src/OnvifWebhookBridge/Persistence/Migrations") -Filter "*.sql" | ForEach-Object { "src/OnvifWebhookBridge/Persistence/Migrations/$($_.Name)" }
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
RUN dotnet publish src/OnvifWebhookBridge/OnvifWebhookBridge.csproj -c Release -r linux-x64 -o /app/bridge
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:da5846024d0008b48259b53a8953d39381fe5b7ec99ef1449ba4ba927f201490
WORKDIR /app
COPY --from=build /app/publish/ ./
COPY --from=build /app/bridge/ /bridge/
RUN mkdir -p /ownership-fixtures/directory /ownership-fixtures/database /ownership-fixtures/wal /ownership-fixtures/shm \
    && chmod 0700 /ownership-fixtures/* \
    && chown $APP_UID /ownership-fixtures/database /ownership-fixtures/wal /ownership-fixtures/shm \
    && touch /ownership-fixtures/database/configuration.db /ownership-fixtures/wal/configuration.db /ownership-fixtures/wal/configuration.db-wal /ownership-fixtures/shm/configuration.db /ownership-fixtures/shm/configuration.db-shm \
    && chown $APP_UID /ownership-fixtures/wal/configuration.db /ownership-fixtures/shm/configuration.db \
    && chmod 0600 /ownership-fixtures/*/configuration.db*
ENV ONVIF_TEST_OWNERSHIP_FIXTURES=/ownership-fixtures ONVIF_TEST_BRIDGE_EXECUTABLE=/bridge/OnvifWebhookBridge
USER $APP_UID
ENTRYPOINT ["./OnvifWebhookBridge.NativeSmoke"]
'@ | Set-Content -LiteralPath (Join-Path $stage 'Dockerfile')
$architecture = $Platform.Split('/')[-1]
$rid = if ($architecture -eq 'amd64') { 'linux-x64' } else { 'linux-arm64' }
$dockerfile = Join-Path $stage 'Dockerfile'
if ($CrossCompile) {
    if ($Platform -ne 'linux/arm64') { throw 'Cross compilation is only configured for arm64 on amd64.' }
    docker build --platform linux/arm64 --target toolchain -t onvif-bridge-tests/toolchain:arm64 $repository
    if ($LASTEXITCODE -ne 0) { throw 'Cross toolchain build failed.' }
    $source = Get-Content $dockerfile -Raw
    $source = $source.Replace('FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build', 'FROM onvif-bridge-tests/toolchain:arm64 AS build')
    $source = $source.Replace('RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*', '')
    Set-Content $dockerfile $source
}
(Get-Content $dockerfile -Raw).Replace('linux-x64', $rid) | Set-Content $dockerfile
$testImage = "onvif-bridge-tests/phase7-native:$architecture"
docker build --platform $Platform --tag $testImage $stage
if ($LASTEXITCODE -ne 0) { throw 'Native test image build failed.' }
if ($Platform -eq "linux/amd64") { docker tag $testImage onvif-bridge-tests/phase7-native }
if ($StorageOnly) {
    docker run --rm --platform $Platform $testImage --storage-only
    if ($LASTEXITCODE -ne 0) { throw "Native storage verification failed." }
    return
}
$revision = '8f86706c2ba768006e85b38f6bc1c760d36f1bba'
$bind = [Net.IPAddress]::Any.ToString()
$simulator = docker run --detach --rm "onvif-bridge-tests/onvifsim:$revision" --cameras 1 --bind $bind --control-bind $bind --no-discovery
if ($LASTEXITCODE -ne 0) { throw 'Simulator startup failed.' }
try {
    $device = [UriBuilder]::new('http', [Net.IPAddress]::Loopback.ToString(), 8000, '/onvif/device_service').Uri.AbsoluteUri
    $control = [UriBuilder]::new('http', [Net.IPAddress]::Loopback.ToString(), 9000).Uri.AbsoluteUri
    $second = [UriBuilder]::new('http', [Net.IPAddress]::Loopback.ToString(), 8001, '/onvif/device_service').Uri.AbsoluteUri
    $emulation = if ($CrossCompile) { "1" } else { "0" }
    docker run --rm --platform $Platform --env "ONVIF_TEST_EMULATION=$emulation" --network "container:$simulator" $testImage $device $control $second
    if ($LASTEXITCODE -ne 0) { throw 'Native simulator verification failed.' }
}
finally {
    docker stop $simulator | Out-Null
}
