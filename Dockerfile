# syntax=docker/dockerfile:1@sha256:4edf897a3ffa55b89f906fc8cc78afdb3f1834cc9c7083565e611a8a7d5fe99e
# CI uses native platform runners. Local amd64 builders can cross-link arm64,
# then execute the resulting arm64 binary under explicitly labelled emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS toolchain
ARG TARGETARCH
ARG BUILDARCH
RUN test "$TARGETARCH" = amd64 || test "$TARGETARCH" = arm64
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
RUN if [ "$BUILDARCH" != "$TARGETARCH" ]; then \
      test "$BUILDARCH/$TARGETARCH" = amd64/arm64 && \
      dpkg --add-architecture arm64 && \
      sed -i '/^Types: deb/a Architectures: amd64' /etc/apt/sources.list.d/ubuntu.sources && \
      printf 'deb [arch=arm64] http://ports.ubuntu.com/ubuntu-ports noble main universe\ndeb [arch=arm64] http://ports.ubuntu.com/ubuntu-ports noble-updates main universe\ndeb [arch=arm64] http://ports.ubuntu.com/ubuntu-ports noble-security main universe\n' > /etc/apt/sources.list.d/arm64.list && \
      apt-get update && apt-get install -y --no-install-recommends binutils-aarch64-linux-gnu gcc-aarch64-linux-gnu zlib1g-dev:arm64 && \
      ln -s /usr/bin/aarch64-linux-gnu-objcopy /usr/local/bin/objcopy && \
      rm -rf /var/lib/apt/lists/*; \
    fi
FROM toolchain AS build
ARG TARGETARCH
ARG VERSION
ARG REVISION
# Match the workflow counter used by release.py and host MSBuild.
ARG GITHUB_RUN_NUMBER
WORKDIR /source
COPY Directory.Build.props Directory.Build.targets ./
COPY src/OnvifWebhookBridge/OnvifWebhookBridge.csproj src/OnvifWebhookBridge/
RUN dotnet restore src/OnvifWebhookBridge/OnvifWebhookBridge.csproj -a $TARGETARCH
COPY src/OnvifWebhookBridge/ src/OnvifWebhookBridge/
# An optional label input must agree with the source; it never overrides MSBuild's version.
RUN test -z "$VERSION" || test "$VERSION" = "$(dotnet msbuild src/OnvifWebhookBridge/OnvifWebhookBridge.csproj -getProperty:Version)"
RUN dotnet publish src/OnvifWebhookBridge/OnvifWebhookBridge.csproj \
    --configuration Release --output /app/publish -a $TARGETARCH -p:StripSymbols=true -p:SourceRevisionId=$REVISION \
    && rm -f /app/publish/*.dbg

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:da5846024d0008b48259b53a8953d39381fe5b7ec99ef1449ba4ba927f201490 AS final
ARG VERSION
ARG REVISION
LABEL org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION \
      org.opencontainers.image.source="https://github.com/dombrovsky/onvif-webhook-bridge" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /app/publish/ ./
RUN mkdir /data && chown 1654:1654 /data && chmod 0700 /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    ONVIF_DATA_DIRECTORY=/data
USER 1654:1654
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["./OnvifWebhookBridge", "--health-probe"]
ENTRYPOINT ["./OnvifWebhookBridge"]
