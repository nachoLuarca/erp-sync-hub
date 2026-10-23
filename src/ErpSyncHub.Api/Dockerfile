# Contexto de build: raíz del repositorio (necesita Directory.Build.props, Directory.Packages.props y global.json).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ErpSyncHub.Domain/ErpSyncHub.Domain.csproj src/ErpSyncHub.Domain/
COPY src/ErpSyncHub.Application/ErpSyncHub.Application.csproj src/ErpSyncHub.Application/
COPY src/ErpSyncHub.Infrastructure/ErpSyncHub.Infrastructure.csproj src/ErpSyncHub.Infrastructure/
COPY src/SapB1.Client/SapB1.Client.csproj src/SapB1.Client/
COPY src/ErpSyncHub.Api/ErpSyncHub.Api.csproj src/ErpSyncHub.Api/
RUN dotnet restore src/ErpSyncHub.Api/ErpSyncHub.Api.csproj

COPY src/ErpSyncHub.Domain/ src/ErpSyncHub.Domain/
COPY src/ErpSyncHub.Application/ src/ErpSyncHub.Application/
COPY src/ErpSyncHub.Infrastructure/ src/ErpSyncHub.Infrastructure/
COPY src/SapB1.Client/ src/SapB1.Client/
COPY src/ErpSyncHub.Api/ src/ErpSyncHub.Api/
RUN dotnet publish src/ErpSyncHub.Api/ErpSyncHub.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# /health responde 200 también cuando SAP está caído (estado Degraded) y 503 solo si falla PostgreSQL.
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "ErpSyncHub.Api.dll"]
