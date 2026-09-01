# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props KodiMCPSharp.slnx ./
COPY src/KodiMCPSharp/KodiMCPSharp.csproj src/KodiMCPSharp/
ARG TARGETARCH
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    dotnet restore src/KodiMCPSharp/KodiMCPSharp.csproj -r "linux-$arch"

COPY src/KodiMCPSharp/ src/KodiMCPSharp/
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    dotnet publish src/KodiMCPSharp/KodiMCPSharp.csproj \
      -c Release -r "linux-$arch" --self-contained true --no-restore \
      -o /app/publish \
      -p:PublishSingleFile=true \
      -p:EnableCompressionInSingleFile=true \
      -p:DebugType=none -p:DebugSymbols=false
RUN mkdir -p /app/publish/logs /app/publish/kodimcpsharp_data

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra AS runtime
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    KODIMCP_Server__Host=0.0.0.0 \
    KODIMCP_Server__Port=5719 \
    KODIMCP_Server__Path=/mcp

COPY --from=build --chown=$APP_UID:0 /app/publish ./
USER $APP_UID
EXPOSE 5719
VOLUME ["/app/logs", "/app/kodimcpsharp_data"]
ENTRYPOINT ["./KodiMCPSharp"]
