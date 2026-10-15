# Compatibility entry point: preserve earlier checks using the phase 6 source allowlist.
param([switch]$StorageOnly)
& "$PSScriptRoot/Verify-Phase6Native.ps1" -StorageOnly:$StorageOnly
