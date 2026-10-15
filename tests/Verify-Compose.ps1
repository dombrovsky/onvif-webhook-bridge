# Test only: run the shipped Compose definition with synthetic setup and a new volume.
param([string]$Image = 'onvif-webhook-bridge:local')
$ErrorActionPreference='Stop'
$id='onvif-compose-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
$stage=Join-Path (Resolve-Path .) ".local/$id"
New-Item -ItemType Directory -Force $stage | Out-Null
$password=Join-Path $stage 'password'
[IO.File]::WriteAllText($password, 'synthetic-admin-password')
$names=@('ONVIF_IMAGE','ONVIF_ADMIN_USERNAME','ONVIF_ADMIN_PASSWORD_PATH','ONVIF_PUBLISHED_PORT','ONVIF_CONTAINER_PORT','ONVIF_BIND_ADDRESS')
$saved=@{}
foreach ($name in $names) { $saved[$name]=[Environment]::GetEnvironmentVariable($name) }
$env:ONVIF_IMAGE=$Image
$env:ONVIF_ADMIN_USERNAME='synthetic-admin'
$env:ONVIF_ADMIN_PASSWORD_PATH="./.local/$id/password"
$env:ONVIF_PUBLISHED_PORT='0'
$env:ONVIF_CONTAINER_PORT='8188'
$env:ONVIF_BIND_ADDRESS=[Net.IPAddress]::Loopback.ToString()
# Avoid importing an operator's .env while testing the shipped definition.
$environmentFile=Join-Path $stage 'empty.env'
$composeFile=Join-Path (Resolve-Path .) 'docker-compose.yml'
[IO.File]::WriteAllText($environmentFile, '')
function Compose {
    $result=& docker compose -f $composeFile --env-file $environmentFile -p $id @args
    if ($LASTEXITCODE -ne 0) { throw 'Compose verification operation failed.' }
    $result
}
function Login([string]$value, [int]$expected) {
    $origin='http://'+(Compose port bridge 8188)
    $body=@{username='synthetic-admin';password=$value} | ConvertTo-Json -Compress
    $response=Invoke-WebRequest -Method Post -Uri "$origin/api/login" -Headers @{Origin=$origin} -ContentType application/json -Body $body -SkipHttpErrorCheck -TimeoutSec 30
    if ([int]$response.StatusCode -ne $expected) { throw 'Compose administrator credential assertion failed.' }
}
try {
    Compose config --quiet
    Compose up -d --no-build | Out-Null
    $container=Compose ps -q bridge
    Start-Sleep -Seconds 2
    $mapping=Compose port bridge 8188
    if (!$mapping.StartsWith([Net.IPAddress]::Loopback.ToString()+':')) { throw 'Compose must publish to loopback.' }
    $ready=$false
    for ($i=0; $i -lt 60; $i++) {
        & docker exec $container ./OnvifWebhookBridge --health-probe
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw 'Compose startup/secret/mount health failed.' }
    $inspect=(& docker inspect $container | ConvertFrom-Json)[0]
    if (!$inspect.HostConfig.ReadonlyRootfs -or $inspect.HostConfig.RestartPolicy.Name -ne 'unless-stopped' -or $inspect.Config.User -ne '1654:1654') { throw 'Compose runtime restrictions failed.' }
    Login 'synthetic-admin-password' 200
    Compose stop | Out-Null
    [IO.File]::WriteAllText($password, 'synthetic-replacement-password')
    Compose run --rm --no-deps bridge --reset-administrator | Out-Null
    Compose up -d --no-build --force-recreate | Out-Null
    $container=Compose ps -q bridge
    $ready=$false
    for ($i=0; $i -lt 60; $i++) {
        & docker exec $container ./OnvifWebhookBridge --health-probe
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw 'Compose reset/recreation failed.' }
    Login 'synthetic-admin-password' 401
    Login 'synthetic-replacement-password' 200
    Write-Output 'PASS: actual Compose, file-secret bootstrap, loopback/random published and configured internal ports, named volume, read-only root, UID, restart policy and changed-password offline reset/recreation.'
}
finally {
    & docker compose -f $composeFile --env-file $environmentFile -p $id down --volumes | Out-Null
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
