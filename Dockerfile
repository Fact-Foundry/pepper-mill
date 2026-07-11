# syntax=docker/dockerfile:1
# ── PepperMill container image ─────────────────────────────────────────────
# Multi-stage: build with the SDK, ship on the smaller ASP.NET runtime as a
# non-root user. The pepper store lives on a mounted volume at /data, never
# baked into the image.

# 1) Build & publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless the csproj changes)
COPY src/FactFoundry.PepperMill/FactFoundry.PepperMill.csproj src/FactFoundry.PepperMill/
RUN dotnet restore src/FactFoundry.PepperMill/FactFoundry.PepperMill.csproj

# Then copy the rest and publish
COPY . .
RUN dotnet publish src/FactFoundry.PepperMill/FactFoundry.PepperMill.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# 2) Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is only here to power the container HEALTHCHECK against /health
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Encrypted pepper store + audit log live here; owned by the non-root app user
# so the mounted volume is writable without running as root.
RUN mkdir -p /data/peppers && chown -R app:app /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    PepperMill__StorePath=/data/peppers
# ASPNETCORE_ENVIRONMENT defaults to Production in this image — which is what we
# want: PepperMill refuses to start without a real StorageKeyBase64.

EXPOSE 8080
VOLUME /data
USER app

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "FactFoundry.PepperMill.dll"]
