# Validate GitHub workflow syntax/expressions locally; never dispatches a workflow.
$ErrorActionPreference='Stop'
$image='rhysd/actionlint:1.7.7@sha256:887a259a5a534f3c4f36cb02dca341673c6089431057242cdc931e9f133147e9'
foreach ($path in @('.github/workflows/ci.yml','.github/workflows/images.yml')) {
    Get-Content -Raw $path | docker run --rm -i $image -
    if ($LASTEXITCODE -ne 0) { throw "Workflow validation failed: $path" }
}
Write-Output 'PASS: workflow syntax, expressions and action inputs (local actionlint; no hosted execution).'
