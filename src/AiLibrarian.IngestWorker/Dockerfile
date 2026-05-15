# syntax=docker/dockerfile:1
# Build from repository root:
#   docker build -f src/AiLibrarian.IngestWorker/Dockerfile -t ailib-ingest:local .
#
# Image shape mirrors src/AiLibrarian.Api/Dockerfile. The worker is a
# console host (Microsoft.NET.Sdk.Worker), so no ingress, no port exposure.
# Service Bus connectivity is the readiness signal; Container Apps watches
# the container process itself.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Packages.props Directory.Build.props AiLibrarian.sln ./
COPY src/ ./src/
RUN dotnet publish src/AiLibrarian.IngestWorker/AiLibrarian.IngestWorker.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
RUN groupadd --system app && useradd --system --gid app app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "AiLibrarian.IngestWorker.dll"]
