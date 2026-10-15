# Build the upstream Dockerfile at the reviewed simulator revision.
$ErrorActionPreference = 'Stop'
$revision = '8f86706c2ba768006e85b38f6bc1c760d36f1bba'
docker build --file packaging/docker/Dockerfile `
    --tag "onvif-bridge-tests/onvifsim:$revision" `
    "https://github.com/mrtian2016/onvifsim.git#$revision"
if ($LASTEXITCODE -ne 0) { throw 'Simulator image build failed.' }
