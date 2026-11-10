# Pucksley hub for Linux / Docker (e.g. a Synology NAS with Container Manager, or a Raspberry Pi).
# The Windows-only plugins (PC audio, PC control, Windows voices) aren't included: they need the
# hub to run on the Windows PC itself.
#
#   docker build -t pucksley-hub -f hub/Dockerfile hub
#   docker compose -f hub/docker-compose.yml up -d
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Hosts/Pucksley.Server/Pucksley.Server.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# libgomp1: the OpenMP runtime local Whisper's CPU backend needs
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Settings, secrets, plugin data, logs and downloaded speech models live here: mount a volume
ENV PUCKSLEY_DATA=/data \
    DOTNET_RUNNING_IN_CONTAINER=true \
    PUCKSLEY_NO_USB=1
VOLUME ["/data"]
EXPOSE 8765
ENTRYPOINT ["dotnet", "pucksley-hub.dll"]
