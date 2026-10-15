# Disposable production-image verification. No camera or external action addresses accepted.
param([string]$Image = 'onvif-webhook-bridge:local',
    [ValidateSet('linux/amd64','linux/arm64')][string]$Platform = 'linux/amd64',
    [ValidateRange(1,3600)][int]$SoakSeconds = 120, [string]$PlaywrightModule)
$ErrorActionPreference = 'Stop'
$dockerExecutable = (Get-Command docker -CommandType Application | Select-Object -First 1).Source
function Docker {
    $result = & $script:dockerExecutable @args
    if ($LASTEXITCODE -ne 0) { throw 'Deployment Docker operation failed.' }
    return $result
}
function Check($value, $label) { if (!$value) { throw "Deployment assertion: $label" } }
function Wait-Until([scriptblock]$check, [string]$label, [int]$seconds = 45) {
    $limit = [DateTime]::UtcNow.AddSeconds($seconds)
    do { if (& $check) { return }; Start-Sleep -Milliseconds 300 } while ([DateTime]::UtcNow -lt $limit)
    throw "Deployment timeout: $label"
}
$id = 'onvif-phase8-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$network = "$id-network"; $volume = "$id-data"
$containers = [Collections.Generic.List[string]]::new()
$architecture = $Platform.Split('/')[-1]
$loopback = [Net.IPAddress]::Loopback.ToString()
$any = [Net.IPAddress]::Any.ToString()
$revision = '8f86706c2ba768006e85b38f6bc1c760d36f1bba'
$port = 8187
$session = $null; $csrf = $null
$trust = $false
$stage = Join-Path (Resolve-Path .) ".local/$id"
New-Item -ItemType Directory -Force $stage | Out-Null
function Api($method, $path, $body = $null) {
    $options = @{ Method=$method; Uri="$script:origin$path"; WebSession=$script:session; Headers=@{Origin=$script:origin; 'X-CSRF-Token'=$script:csrf}; TimeoutSec=15 }
    if ($null -ne $body) { $options.ContentType='application/json'; $options.Body=ConvertTo-Json $body -Depth 20 -Compress }
    $response = Invoke-RestMethod @options
    return $response
}
function Start-Bridge {
    $name = "$id-app"
    $startup = [Diagnostics.Stopwatch]::StartNew()
    Docker run -d --name $name --platform $Platform --network $network --read-only --cap-drop ALL --security-opt no-new-privileges --tmpfs "/tmp:rw,noexec,nosuid,size=32m" --mount "type=volume,src=$volume,dst=/data" -p "${loopback}::${port}" -e "ASPNETCORE_HTTP_PORTS=$port" -e SSL_CERT_FILE=/tmp/synthetic-ca.pem -e ONVIF_ADMIN_USERNAME=synthetic-admin -e ONVIF_ADMIN_PASSWORD=synthetic-admin-password $Image | Out-Null
    $script:containers.Add($name)
    if ($script:trust) {
        Get-Content -Raw (Join-Path $stage 'synthetic-ca.pem') | & $script:dockerExecutable exec -i $name sh -c 'cat > /tmp/synthetic-ca.pem'
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic CA copy to writable tmpfs failed.' }
    }
    $mapped = (Docker port $name "$port/tcp" | Select-Object -First 1).Split(':')[-1]
    $script:origin = [UriBuilder]::new('http',$loopback,[int]$mapped).Uri.GetLeftPart([UriPartial]::Authority)
    Wait-Until { try { (Invoke-WebRequest "$script:origin/health" -TimeoutSec 2).StatusCode -eq 200 } catch { $false } } 'production health'
    Write-Output ("Startup-to-health: {0:N2}s" -f $startup.Elapsed.TotalSeconds)
    Docker stats --no-stream --format 'idle {{.CPUPerc}} {{.MemUsage}}' $name
    $script:session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $login = Invoke-RestMethod -Method Post -Uri "$script:origin/api/login" -WebSession $script:session -Headers @{Origin=$script:origin} -ContentType application/json -Body '{"username":"synthetic-admin","password":"synthetic-admin-password"}'
    $script:csrf = $login.csrfToken
}
try {
    Docker network create $network | Out-Null
    Docker volume create $volume | Out-Null
    $sim = "$id-simulator"; $rec = "$id-recorder"
    Docker run -d --name $sim --network $network --network-alias simulator -p "${loopback}::9000" "onvif-bridge-tests/onvifsim:$revision" --cameras 1 --bind $any --control-bind $any --no-discovery | Out-Null
    $containers.Add($sim)
    Docker run -d --name $rec --platform $Platform --network $network --network-alias recorder -p "${loopback}::8081" "onvif-bridge-tests/phase7-native:$architecture" --recorder | Out-Null
    $containers.Add($rec)
    $controlPort = (Docker port $sim 9000/tcp | Select-Object -First 1).Split(':')[-1]
    $control = [UriBuilder]::new('http',$loopback,[int]$controlPort).Uri.GetLeftPart([UriPartial]::Authority)
    $recPort = (Docker port $rec 8081/tcp | Select-Object -First 1).Split(':')[-1]
    $recorder = [UriBuilder]::new('http',$loopback,[int]$recPort).Uri.GetLeftPart([UriPartial]::Authority)
    Wait-Until { try { (Invoke-WebRequest "$control/api/scenario" -TimeoutSec 2).StatusCode -eq 200 } catch { $false } } 'simulator'
    $cameras = foreach ($index in 0..1) { @{id="synthetic-$index"; displayName="Synthetic $index"; persona='generic'; users=@(@{username='synthetic-user';password='synthetic-password';level='Administrator'}); network=@{bindAddress=$any;httpPort=8000+$index;advertisedHost='simulator';advertisedHttpPort=8000+$index;rtspPort=8554+$index}} }
    $scenario = @{formatVersion=1;config=@{bindAddress=$any;controlApiAddress=$any;discoveryEnabled=$false};cameras=@($cameras)}
    Invoke-RestMethod -Method Post -Uri "$control/api/scenario" -ContentType application/json -Body (ConvertTo-Json $scenario -Depth 15 -Compress) | Out-Null
    Wait-Until { try { (Invoke-WebRequest "$recorder/count" -TimeoutSec 2).StatusCode -eq 200 } catch { $false } } 'recorder'
    Docker cp "${rec}:/tmp/synthetic-ca.pem" (Join-Path $stage 'synthetic-ca.pem') | Out-Null
    Start-Bridge
    $app = "$id-app"
    Check ((Docker exec $app id -u) -eq '1654') 'non-root UID'
    Check ((Docker exec $app stat -c '%a:%u' /data) -eq '700:1654') 'private mounted directory'
    Check ((Docker exec $app stat -c '%a:%u' /data/configuration.db) -eq '600:1654') 'private mounted database'
    Docker exec $app ./OnvifWebhookBridge --health-probe | Out-Null
    Check ((Invoke-WebRequest "$origin/").Content.Contains('app.js')) 'packaged browser shell'
    foreach ($index in 0..1) {
        $cfg = Api GET /api/configuration
        Api PUT "/api/cameras/synthetic-$index" @{revision=$cfg.revision;name="Synthetic $index";endpoint="http://simulator:$(8000+$index)/onvif/device_service";enabled=$true;username=@{operation=1;value='synthetic-user'};password=@{operation=1;value='synthetic-password'}} | Out-Null
        $cfg = Api GET /api/configuration
        Api PUT "/api/rules/synthetic-$index" @{revision=$cfg.revision;name="Synthetic rule $index";cameraId="synthetic-$index";topic='Synthetic/Deployment';enabled=$true;method='POST';url=@{operation=1;value='http://recorder:8081/synthetic'};jsonBody=@{operation=1;value='{"synthetic":true}'}} | Out-Null
    }
    Wait-Until { @(Api GET /api/camera-status | Where-Object state -eq Subscribed).Count -eq 2 } 'two bridge-network subscriptions'
    function Trigger($index) { Invoke-RestMethod -Method Post -Uri "$control/api/cameras/synthetic-$index/events" -ContentType application/json -Body '{"topic":"Synthetic/Deployment","state":true,"duration":0}' | Out-Null }
    $before = [int](Invoke-RestMethod "$recorder/count")
    Trigger 0; Trigger 1
    Wait-Until { [int](Invoke-RestMethod "$recorder/count") -ge $before+2 } 'production event to recorder'
    if ($PlaywrightModule) {
        $env:ONVIF_BROWSER_ORIGIN=$origin
        $env:ONVIF_PLAYWRIGHT_MODULE=$PlaywrightModule
        node tests/PackagingWalkthrough.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Production browser walkthrough failed.' }
    }
    $until = [DateTime]::UtcNow.AddSeconds($SoakSeconds)
    $cycles = 0
    while ([DateTime]::UtcNow -lt $until) {
        foreach ($index in 0..1) {
            $before = [int](Invoke-RestMethod "$recorder/count")
            $previous = Api GET /api/executions | Where-Object ruleId -eq "synthetic-$index" | Select-Object -ExpandProperty jobId
            Trigger $index
            Wait-Until {
                $completed = @(Api GET /api/executions | Where-Object {
                    $_.ruleId -eq "synthetic-$index" -and $_.jobId -ne $previous -and $_.result -eq 2 -and $_.statusCode -eq 204
                })
                $completed.Count -eq 1 -and [int](Invoke-RestMethod "$recorder/count") -ge $before+1
            } "soak delivery from camera $index"
        }
        Check (@(Api GET /api/camera-status | Where-Object state -eq Subscribed).Count -eq 2) 'soak subscriptions'
        Docker exec $app ./OnvifWebhookBridge --health-probe | Out-Null
        $cycles++
        Start-Sleep -Seconds 5
    }
    Write-Output "Verified soak cycles with delivery from each camera: $cycles"
    $cfg = Api GET /api/configuration
    Api PUT /api/rules/synthetic-tls @{revision=$cfg.revision;name='Synthetic TLS';cameraId='synthetic-0';topic='Synthetic/TLS';enabled=$false;url=@{operation=1;value='https://recorder:8443/synthetic'}} | Out-Null
    Api POST /api/rules/synthetic-tls/test | Out-Null
    Wait-Until { @(Api GET /api/executions | Where-Object { $_.ruleId -eq 'synthetic-tls' -and $_.result -eq 4 }).Count -eq 1 } 'untrusted TLS rejection'
    Docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}}' $app
    # Actual disconnected simulator leaves process/persistence healthy.
    Docker stop --time 5 $sim | Out-Null
    Docker exec $app ./OnvifWebhookBridge --health-probe | Out-Null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Docker stop --time 15 $app | Out-Null
    Check ($watch.Elapsed.TotalSeconds -lt 15) 'bounded graceful stop'
    Check ((Docker inspect --format '{{.State.ExitCode}}' $app) -eq '0') 'clean SIGTERM exit'
    Docker rm $app | Out-Null; $containers.Remove($app) | Out-Null
    Docker start $sim | Out-Null
    $controlPort = (Docker port $sim 9000/tcp | Select-Object -First 1).Split(':')[-1]
    $control = [UriBuilder]::new('http',$loopback,[int]$controlPort).Uri.GetLeftPart([UriPartial]::Authority)
    Wait-Until { try { (Invoke-WebRequest "$control/api/scenario" -TimeoutSec 2).StatusCode -eq 200 } catch { $false } } 'restarted simulator'
    Invoke-RestMethod -Method Post -Uri "$control/api/scenario" -ContentType application/json -Body (ConvertTo-Json $scenario -Depth 15 -Compress) | Out-Null
    $trust = $true
    Start-Bridge
    $cfg = Api GET /api/configuration
    Check ($cfg.cameras.Count -eq 2 -and $cfg.rules.Count -eq 3) 'mounted restore after recreation'
    Check ($cfg.rules[0].hasJsonBody -and !$cfg.rules[0].PSObject.Properties['url']) 'masked restored secrets'
    Docker exec $app ./OnvifWebhookBridge --health-probe | Out-Null
    Api POST /api/rules/synthetic-tls/test | Out-Null
    Wait-Until { @(Api GET /api/executions | Where-Object { $_.ruleId -eq 'synthetic-tls' -and $_.statusCode -eq 204 }).Count -eq 1 } 'trusted TLS acceptance'
    Wait-Until { @(Api GET /api/camera-status | Where-Object state -eq Subscribed).Count -eq 2 } 'restored subscriptions'
    $before = [int](Invoke-RestMethod "$recorder/count")
    Trigger 0; Trigger 1
    Wait-Until { [int](Invoke-RestMethod "$recorder/count") -ge $before+2 } 'restored delivery'
    Docker image inspect --format '{{.Architecture}} {{.Size}}' $Image
    Write-Output 'PASS: production bridge-network onvifsim/recorder, two cameras, mounted persistence, non-root/modes, browser assets, health, configured port, outage health, SIGTERM and recreation.'
}
catch {
    $failure = $_
    if ($session) {
        try { Api GET /api/camera-status | ConvertTo-Json -Depth 5 | Write-Output } catch { }
    }
    throw $failure
}
finally {
    foreach ($name in $containers) { & docker rm -f $name | Out-Null }
    & docker volume rm $volume | Out-Null
    & docker network rm $network | Out-Null
}
