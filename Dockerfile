# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /source
COPY Directory.Build.props Directory.Build.targets ./
COPY src/OnvifWebhookBridge/OnvifWebhookBridge.csproj src/OnvifWebhookBridge/
RUN dotnet restore src/OnvifWebhookBridge/OnvifWebhookBridge.csproj
COPY src/OnvifWebhookBridge/ src/OnvifWebhookBridge/
RUN dotnet publish src/OnvifWebhookBridge/OnvifWebhookBridge.csproj \
    --configuration Release --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:da5846024d0008b48259b53a8953d39381fe5b7ec99ef1449ba4ba927f201490 AS final
WORKDIR /app
COPY --from=build /app/publish/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["./OnvifWebhookBridge"]
