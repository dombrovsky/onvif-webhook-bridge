# Test the shipped published-image installation from a directory containing only YAML.
# -Pull tests the real registry image; CI may pass its locally verified image instead.
param(
    [string]$Image = 'dombrovskyi/onvif-webhook-bridge:latest',
    [switch]$Pull,
    [string]$ExpectedVersion,
    [string]$ExpectedDigest,
    [string]$PlaywrightModule
)
$ErrorActionPreference = 'Stop'
$id = 'onvif-compose-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$stage = Join-Path (Resolve-Path .) ".local/$id"
New-Item -ItemType Directory -Force $stage | Out-Null
$composeFile = Join-Path $stage 'docker-compose.yml'
Copy-Item docker-compose.yml $composeFile
$environmentFile = Join-Path $stage 'empty.env'
[IO.File]::WriteAllText($environmentFile, '')
$names = @('ONVIF_IMAGE','ONVIF_ADMIN_USERNAME','ONVIF_BOOTSTRAP_VOLUME','ONVIF_PUBLISHED_PORT','ONVIF_CONTAINER_PORT','ONVIF_BIND_ADDRESS')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$env:ONVIF_IMAGE = $Image
$env:ONVIF_ADMIN_USERNAME = 'synthetic-admin'
$env:ONVIF_BOOTSTRAP_VOLUME = "$id-bootstrap"
$env:ONVIF_PUBLISHED_PORT = '0'
$env:ONVIF_CONTAINER_PORT = '8188'
$env:ONVIF_BIND_ADDRESS = '127.0.0.1'
function Invoke-Docker {
    $result = & docker @args
    if ($LASTEXITCODE -ne 0) { throw 'Docker verification operation failed.' }
    $result
}
function Compose { Invoke-Docker compose -f $composeFile --env-file $environmentFile -p $id @args }
function Ready {
    $container = Compose ps -q bridge
    for ($i = 0; $i -lt 90; $i++) {
        $state = (Invoke-Docker inspect $container | ConvertFrom-Json)[0].State
        if ($state.Health.Status -eq 'healthy') { return $container }
        if ($state.Status -eq 'exited') { throw 'Compose service exited.' }
        Start-Sleep -Seconds 1
    }
    throw 'Compose did not become Docker healthy.'
}
function Login([string]$value, [int]$expected = 200) {
    $script:origin = 'http://' + (Compose port bridge 8188)
    $body = @{username='synthetic-admin';password=$value} | ConvertTo-Json -Compress
    $response = Invoke-WebRequest -Method Post -Uri "$origin/api/login" -Headers @{Origin=$origin} -ContentType application/json -Body $body -SkipHttpErrorCheck -TimeoutSec 30 -SessionVariable session
    if ([int]$response.StatusCode -ne $expected) { throw 'Compose administrator credential assertion failed.' }
    if ($expected -eq 200) {
        $script:webSession = $session
        $script:csrf = ($response.Content | ConvertFrom-Json).csrfToken
    }
}
function InitializePassword([string]$value) {
    # Same hidden-prompt shell operation as README, with synthetic stdin and no TTY.
    $command = 'umask 077; IFS= read -r -s -p "Administrator password: " password && printf "\n" && test "${#password}" -ge 12 && test "${#password}" -le 4096 && printf "%s" "$password" > /data/administrator-password'
    $value | & docker run --rm -i --mount "type=volume,src=$id-bootstrap,dst=/data" --entrypoint /bin/bash $Image -c $command | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Private password-volume initialization failed.' }
}
try {
    if ($Pull) { Invoke-Docker pull --platform linux/amd64 $Image | Out-Null }
    $imageInfo = (Invoke-Docker image inspect $Image | ConvertFrom-Json)[0]
    if ($ExpectedDigest -and !($imageInfo.RepoDigests | Where-Object { $_.EndsWith("@$ExpectedDigest") })) { throw 'Pulled image digest mismatch.' }
    Compose config --quiet
    $configuration = Compose config --format json | ConvertFrom-Json
    if ($configuration.services.bridge.build) { throw 'Public installation must never build.' }
    InitializePassword '  synthetic-admin-password  '
    Compose up -d --no-build | Out-Null
    $container = Ready
    if (!(Compose port bridge 8188).StartsWith('127.0.0.1:')) { throw 'Compose must publish to loopback.' }
    $inspect = (Invoke-Docker inspect $container | ConvertFrom-Json)[0]
    if ($inspect.Image -ne $imageInfo.Id -or !$inspect.HostConfig.ReadonlyRootfs -or $inspect.HostConfig.RestartPolicy.Name -ne 'unless-stopped' -or $inspect.Config.User -ne '1654:1654') { throw 'Image identity/runtime restrictions failed.' }
    $mount = $inspect.Mounts | Where-Object Destination -eq /data
    $bootstrap = $inspect.Mounts | Where-Object Destination -eq /run/bootstrap
    if ($bootstrap.RW -or $bootstrap.Name -ne "$id-bootstrap") { throw 'Bootstrap mount must be private and read-only.' }
    Invoke-Docker exec $container /bin/bash -c 'test "$(stat -c %a /data)" = 700 && test "$(stat -c %a /run/bootstrap/administrator-password)" = 600' | Out-Null
    $version = Invoke-Docker run --rm $Image --version
    if ($ExpectedVersion -and $version -ne $ExpectedVersion) { throw 'Executable version mismatch.' }
    Login '  synthetic-admin-password  '
    $ui = Invoke-WebRequest "$origin/" -TimeoutSec 10
    if ($ui.StatusCode -ne 200 -or $ui.Content -notmatch 'app.js') { throw 'Embedded UI failed.' }
    $config = Invoke-RestMethod "$origin/api/configuration" -WebSession $webSession
    $camera = @{revision=$config.revision;name='Synthetic persistence check';endpoint='http://camera.example/onvif/device_service';enabled=$false} | ConvertTo-Json
    Invoke-WebRequest "$origin/api/cameras/synthetic-persistence" -Method Put -WebSession $webSession -Headers @{Origin=$origin;'X-CSRF-Token'=$csrf} -ContentType application/json -Body $camera | Out-Null
    # Rehearse documented pull/recreate while preserving project and volumes.
    Compose stop | Out-Null
    if ($Pull) { Compose pull | Out-Null }
    Compose up -d --no-build --force-recreate | Out-Null
    $container = Ready
    $recreated = (Invoke-Docker inspect $container | ConvertFrom-Json)[0]
    if (($recreated.Mounts | Where-Object Destination -eq /data).Name -ne $mount.Name -or $recreated.Image -ne $imageInfo.Id) { throw 'Recreation changed image or data volume.' }
    Login '  synthetic-admin-password  '
    $cameras = @(Invoke-RestMethod "$origin/api/cameras" -WebSession $webSession)
    if ($cameras.Count -ne 1 -or $cameras[0].name -ne 'Synthetic persistence check' -or $cameras[0].enabled) { throw 'Configuration persistence failed.' }
    if ($PlaywrightModule) {
        & node tests/PublishedInstallBrowser.cjs $PlaywrightModule $origin
        if ($LASTEXITCODE -ne 0) { throw 'Published installation browser test failed.' }
    }
    Compose stop | Out-Null
    InitializePassword 'synthetic-replacement-password'
    Compose run --rm --no-deps bridge --reset-administrator | Out-Null
    Compose up -d --no-build --force-recreate | Out-Null
    $container = Ready
    Login '  synthetic-admin-password  ' 401
    Login 'synthetic-replacement-password'
    Write-Output "PASS: source-free Compose, private password volume, health/UI/login, disabled synthetic configuration persistence, pull/recreation, offline reset; executable $version; image $($imageInfo.Id); registry $($imageInfo.RepoDigests -join ', ')."
}
finally {
    & docker compose -f $composeFile --env-file $environmentFile -p $id down --volumes | Out-Null
    & docker volume rm "$id-bootstrap" | Out-Null
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
