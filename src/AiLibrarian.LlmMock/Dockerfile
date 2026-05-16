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
RUN getent group app || groupadd --system app \
	&& getent passwd app || useradd --system --gid app app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "AiLibrarian.LlmMock.dll"]
