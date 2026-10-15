# Actual production executable configured through standard Kestrel HTTPS settings.
param([string]$Image='onvif-webhook-bridge:local',[ValidateSet('linux/amd64','linux/arm64')][string]$Platform='linux/amd64')
$ErrorActionPreference='Stop'
$id='onvif-tls-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
$stage=Join-Path (Resolve-Path .) ".local/$id"
New-Item -ItemType Directory -Force $stage | Out-Null
$key=[Security.Cryptography.RSA]::Create(2048)
$request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Synthetic inbound TLS',$key,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
$names=[Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
$names.AddIpAddress([Net.IPAddress]::Loopback); $request.CertificateExtensions.Add($names.Build())
$cert=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(1))
$ca=Join-Path $stage 'synthetic-ca.pem'
[IO.File]::WriteAllText($ca,$cert.ExportCertificatePem())
$pfx=[Convert]::ToBase64String($cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12))
$loopback=[Net.IPAddress]::Loopback.ToString()
try {
    docker run -d --name $id --platform $Platform --read-only --tmpfs '/tmp:rw,noexec,nosuid,size=32m' -p "${loopback}::8443" -e ONVIF_DATA_DIRECTORY=/tmp/data -e ONVIF_ADMIN_USERNAME=synthetic-admin -e ONVIF_ADMIN_PASSWORD=synthetic-admin-password -e Kestrel__Endpoints__Http__Url=http://+:8080 -e Kestrel__Endpoints__Https__Url=https://+:8443 -e Kestrel__Certificates__Default__Path=/tmp/synthetic.pfx --entrypoint sh $Image -c 'while [ ! -f /tmp/synthetic.pfx ]; do sleep 0.1; done; exec /app/OnvifWebhookBridge' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'TLS fixture startup failed.' }
    $pfx | docker exec -i $id sh -c 'base64 --ignore-garbage -d > /tmp/synthetic.pfx.tmp && mv /tmp/synthetic.pfx.tmp /tmp/synthetic.pfx'
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic certificate staging failed.' }
    $ready=$false
    for ($i=0;$i -lt 90;$i++) {
        docker exec $id ./OnvifWebhookBridge --health-probe
        if ($LASTEXITCODE -eq 0) {$ready=$true;break}
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) {throw 'Production HTTPS startup failed.'}
    $port=[int]((docker port $id 8443/tcp | Select-Object -First 1).Split(':')[-1])
    $env:ONVIF_TLS_ORIGIN=[UriBuilder]::new('https',$loopback,$port).Uri.GetLeftPart([UriPartial]::Authority)
    $env:ONVIF_TLS_CA=$ca
    node tests/Verify-Tls.cjs
    if ($LASTEXITCODE -ne 0) {throw 'Production HTTPS verification failed.'}
}
finally {
    docker rm -f $id | Out-Null
    $cert.Dispose(); $key.Dispose()
    Remove-Item Env:ONVIF_TLS_ORIGIN,Env:ONVIF_TLS_CA -ErrorAction SilentlyContinue
}
