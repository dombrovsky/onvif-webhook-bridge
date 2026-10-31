# Execute the README Docker run commands with isolated names/port and synthetic input.
param(
    [string]$Image = 'dombrovskyi/onvif-webhook-bridge:latest',
    [switch]$Pull,
    [string]$ExpectedVersion,
    [string]$ExpectedDigest
)
$ErrorActionPreference = 'Stop'
$id = 'onvif-run-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$password = '  synthetic-admin-password  '
$readme = Get-Content -Raw README.md
$initializer = [regex]::Match($readme, "(?m)^docker run --rm -it .*? -c '(.*)'").Groups[1].Value
$startup = [regex]::Match($readme, '(?s)```sh\r?\n(docker run -d .*?)\r?\ndocker ps').Groups[1].Value
if (!$initializer -or !$startup) { throw 'README installation commands not found.' }
$startup = $startup.Replace('onvif-bridge-bootstrap', "$id-bootstrap").Replace('onvif-bridge-data', "$id-data").Replace('--name onvif-bridge', "--name $id").Replace('127.0.0.1:8080:8080', '127.0.0.1::8080')
$parts = [regex]::Replace($startup, '\\\r?\n\s*', ' ') -split '\s+'
$arguments = $parts[1..($parts.Length - 1)]
$arguments[-1] = $Image
function Invoke-Docker {
    $result = & docker @args
    if ($LASTEXITCODE -ne 0) { throw 'Docker run installation verification failed.' }
    $result
}
function Ready {
    for ($i = 0; $i -lt 90; $i++) {
        $info = (Invoke-Docker inspect $id | ConvertFrom-Json)[0]
        if ($info.State.Health.Status -eq 'healthy') { return $info }
        Start-Sleep -Seconds 1
    }
    throw 'README container did not become healthy.'
}
function Login([string]$value = $password, [int]$expected = 200) {
    $script:origin = 'http://' + (Invoke-Docker port $id 8080/tcp)
    $response = Invoke-WebRequest "$origin/api/login" -Method Post -Headers @{Origin=$origin} -ContentType application/json -Body (@{username='admin';password=$value} | ConvertTo-Json -Compress) -SessionVariable session -SkipHttpErrorCheck -TimeoutSec 30
    if ([int]$response.StatusCode -ne $expected) { throw "Administrator login expected HTTP $expected, received $([int]$response.StatusCode)." }
    if ($expected -ne 200) { return }
    $script:webSession = $session
    $script:csrf = ($response.Content | ConvertFrom-Json).csrfToken
}
try {
    if ($Pull) { Invoke-Docker pull $Image | Out-Null }
    $expectedImage = (Invoke-Docker image inspect $Image | ConvertFrom-Json)[0]
    if ($ExpectedDigest -and !($expectedImage.RepoDigests | Where-Object { $_.EndsWith("@$ExpectedDigest") })) { throw 'Published digest mismatch.' }
    $password | & docker run --rm -i --mount "type=volume,src=$id-bootstrap,dst=/data" --entrypoint /bin/bash $Image -c $initializer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'README password initialization failed.' }
    Invoke-Docker @arguments | Out-Null
    $info = Ready
    if ($info.Image -ne $expectedImage.Id -or !$info.HostConfig.ReadonlyRootfs -or $info.Config.User -ne '1654:1654' -or $info.HostConfig.RestartPolicy.Name -ne 'unless-stopped') { throw 'README image/runtime restrictions failed.' }
    if (!(Invoke-Docker port $id 8080/tcp).StartsWith('127.0.0.1:')) { throw 'README port must remain loopback.' }
    Invoke-Docker exec $id /bin/bash -c 'test "$(stat -c %a /data)" = 700 && test "$(stat -c %a /run/bootstrap/administrator-password)" = 600' | Out-Null
    $version = Invoke-Docker exec $id ./OnvifWebhookBridge --version
    if ($ExpectedVersion -and $version -ne $ExpectedVersion) { throw 'README image version mismatch.' }
    Login
    $ui = Invoke-WebRequest "$origin/"
    if ($ui.Content -notmatch 'app.js') { throw 'README UI not served.' }
    $configuration = Invoke-RestMethod "$origin/api/configuration" -WebSession $webSession
    $camera = @{revision=$configuration.revision;name='Synthetic persistence check';endpoint='http://camera.example/onvif/device_service';enabled=$false} | ConvertTo-Json
    Invoke-WebRequest "$origin/api/cameras/synthetic-persistence" -Method Put -WebSession $webSession -Headers @{Origin=$origin;'X-CSRF-Token'=$csrf} -ContentType application/json -Body $camera | Out-Null
    Invoke-Docker stop $id | Out-Null
    Invoke-Docker rm $id | Out-Null
    Invoke-Docker @arguments | Out-Null
    $info = Ready
    Login
    $cameras = @(Invoke-RestMethod "$origin/api/cameras" -WebSession $webSession)
    if ($cameras.Count -ne 1 -or $cameras[0].name -ne 'Synthetic persistence check' -or $cameras[0].enabled) { throw 'Docker run recreation lost saved configuration.' }
    $configurationBeforeReset = (Invoke-WebRequest "$origin/api/configuration" -WebSession $webSession).Content
    Invoke-Docker stop $id | Out-Null
    $oldPassword = $password
    $password = '  synthetic replacement password  '
    $password | & docker run --rm -i --mount "type=volume,src=$id-bootstrap,dst=/data" --entrypoint /bin/bash $Image -c $initializer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Reset password initialization failed.' }
    $deployment = Get-Content -Raw docs/DEPLOYMENT.md
    $reset = [regex]::Match($deployment, '(?m)^docker run --rm --network none .* --reset-administrator').Value
    if (!$reset) { throw 'Documented Docker run reset command not found.' }
    $reset = $reset.Replace('onvif-bridge-data', "$id-data").Replace('onvif-bridge-bootstrap', "$id-bootstrap")
    $resetParts = $reset.Trim() -split '\s+'
    $resetArguments = $resetParts[1..($resetParts.Length - 1)]
    $resetArguments[-2] = $Image
    Invoke-Docker @resetArguments | Out-Null
    Invoke-Docker start $id | Out-Null
    $info = Ready
    Login $oldPassword 401
    Login $password 200
    $configurationAfterReset = (Invoke-WebRequest "$origin/api/configuration" -WebSession $webSession).Content
    if ($configurationAfterReset -cne $configurationBeforeReset) { throw 'Offline reset changed saved configuration.' }
    Write-Output "PASS: README Docker run, whitespace password login, health/UI, runtime restrictions and persistence across container removal/recreation, plus documented offline reset; version $version; image $($info.Image)."
}
finally {
    & docker rm --force $id | Out-Null
    & docker volume rm "$id-data" "$id-bootstrap" | Out-Null
}
