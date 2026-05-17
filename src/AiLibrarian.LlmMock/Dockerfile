# syntax=docker/dockerfile:1
# Build from repository root:
#   docker build -f src/AiLibrarian.LlmMock/Dockerfile -t ailib-llm-mock:local .
#
# Same multi-stage shape as the other three services so the build
# context, COPY layout, and security posture stay consistent. Listens
# on 8080 inside the container; compose maps the host port.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Packages.props Directory.Build.props AiLibrarian.sln ./
COPY src/ ./src/
RUN dotnet publish src/AiLibrarian.LlmMock/AiLibrarian.LlmMock.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# curl is the dependency of the compose-level healthcheck on this
# service. The aspnet:9.0 base is Debian-slim and ships with neither
# curl nor wget; without one of them, `docker compose up` waits the
# full retry budget then declares the container unhealthy and aborts
# dependent services. ~3MB image-size cost is worth it for a mock
# that's already debugging-friendly.
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl \
	&& rm -rf /var/lib/apt/lists/*
RUN getent group app || groupadd --system app \
	&& getent passwd app || useradd --system --gid app app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "AiLibrarian.LlmMock.dll"]
