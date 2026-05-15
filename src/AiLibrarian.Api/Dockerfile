# syntax=docker/dockerfile:1
# Build from repository root:
#   docker build -f src/AiLibrarian.Api/Dockerfile -t ailib-api:local .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Packages.props Directory.Build.props AiLibrarian.sln ./
COPY src/ ./src/
RUN dotnet publish src/AiLibrarian.Api/AiLibrarian.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
RUN groupadd --system app && useradd --system --gid app app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "AiLibrarian.Api.dll"]
