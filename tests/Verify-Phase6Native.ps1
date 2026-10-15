# Compatibility entry point: preserve phase 6 checks using the current source allowlist.
param([switch]$StorageOnly)
& "$PSScriptRoot/Verify-Phase7Native.ps1" -StorageOnly:$StorageOnly
