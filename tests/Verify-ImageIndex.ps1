# Local OCI archive only. No registry writes, tag publication or deployment.
param([string]$Archive = '.local/phase8-images.oci.tar', [switch]$Build)
$ErrorActionPreference='Stop'
if ($Build) {
    docker buildx build --platform linux/amd64,linux/arm64 --output "type=oci,dest=$Archive" .
    if ($LASTEXITCODE -ne 0) { throw 'Local OCI build failed.' }
}
$index=tar -xOf $Archive index.json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'OCI index read failed.' }
# Buildx wraps the platform index in the layout index.
if ($index.manifests.Count -eq 1 -and $index.manifests[0].mediaType -eq 'application/vnd.oci.image.index.v1+json') {
    $digest=$index.manifests[0].digest.Split(':')[-1]
    if ($digest -notmatch '^[a-f0-9]{64}$') { throw 'Invalid OCI digest.' }
    $index=tar -xOf $Archive "blobs/sha256/$digest" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Platform index read failed.' }
}
$platforms=@($index.manifests | Where-Object {$_.platform.os -ne 'unknown'} | ForEach-Object {$_.platform.os+'/'+$_.platform.architecture})
if (($platforms | Sort-Object) -join ',' -ne 'linux/amd64,linux/arm64') { throw 'Unexpected supported platforms.' }
Write-Output 'PASS: local OCI runnable platforms are exactly linux/amd64 and linux/arm64; attestation descriptors excluded.'
