# Contexto de build: raíz del repositorio (necesita Directory.Build.props, Directory.Packages.props y global.json).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SapB1.Mock/SapB1.Mock.csproj src/SapB1.Mock/
RUN dotnet restore src/SapB1.Mock/SapB1.Mock.csproj

COPY src/SapB1.Mock/ src/SapB1.Mock/
RUN dotnet publish src/SapB1.Mock/SapB1.Mock.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=5 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "SapB1.Mock.dll"]
