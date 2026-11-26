# =============================================================================
# Stage 1: Build & Publish (.NET 11 SDK)
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

COPY global.json ./
COPY src/Drvrs.Web/Drvrs.Web.csproj src/Drvrs.Web/
RUN dotnet restore src/Drvrs.Web/Drvrs.Web.csproj

COPY src/Drvrs.Web/ src/Drvrs.Web/
RUN dotnet publish src/Drvrs.Web/Drvrs.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# =============================================================================
# Stage 2: Production Runtime (.NET 11 ASP.NET Core + ffmpeg for WMV->MP4)
# =============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/data \
    && chown -R app:app /app

COPY --from=build --chown=app:app /app/publish ./

USER app

ENV ASPNETCORE_URLS="http://+:8080" \
    DOTNET_RUNNING_IN_CONTAINER="true" \
    Drvrs__DataDirectory="/app/data" \
    Drvrs__AutoSyncQuestionsOnStartup="true" \
    Drvrs__AutoSyncMediaOnStartup="true" \
    Drvrs__ReplaceWmvAfterTranscode="true"

VOLUME ["/app/data"]
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "Drvrs.Web.dll"]
