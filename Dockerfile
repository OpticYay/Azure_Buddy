# syntax=docker/dockerfile:1

# ---- Build stage ----
# Full SDK image only exists to compile/publish; it's discarded after this stage, so its size
# (~800MB) never ends up in the image you actually ship.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj files first and restore before copying the rest of the source - Docker layer caching
# means `dotnet restore` (the slow step, downloading NuGet packages) only reruns when a .csproj
# actually changes, not on every source edit.
COPY AzureBuddy.slnx ./
COPY src/AzureBuddy.Api/AzureBuddy.Api.csproj src/AzureBuddy.Api/
COPY src/AzureBuddy.Core/AzureBuddy.Core.csproj src/AzureBuddy.Core/
COPY src/AzureBuddy.Data/AzureBuddy.Data.csproj src/AzureBuddy.Data/
RUN dotnet restore src/AzureBuddy.Api/AzureBuddy.Api.csproj

COPY src/ src/
RUN dotnet publish src/AzureBuddy.Api/AzureBuddy.Api.csproj -c Release -o /app --no-restore

# ---- Runtime stage ----
# The ASP.NET runtime image (no SDK/compiler) is the smallest base that can actually run a published
# app - keeps the shipped image lean and reduces attack surface versus including build tooling.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Runs as the non-root "app" user baked into Microsoft's .NET 8 images instead of root - if the
# process is ever compromised, it isn't running with more privilege than it needs inside the container.
# The chown has to be a separate step after COPY, not just `COPY --chown` - that only chowns the
# copied files, not the /app directory itself (WORKDIR created it as root before COPY ran), and the
# app still needs write access to /app to create the DataProtection-Keys subdirectory at runtime.
# Confirmed by an actual container run without this: the key ring failed to load (Permission denied)
# and the app fell back to ephemeral, non-persisted keys - meaning every encrypted ADO PAT would
# become unreadable on the next container restart.
COPY --from=build /app .

# DataProtection-Keys doesn't exist in the image - the app only creates it the first time it writes a
# key. That matters because when a *named volume* (e.g. docker-compose.yml's dp-keys) is mounted here
# for the first time, Docker seeds it by copying whatever already exists at this path in the image,
# ownership included; with no pre-existing directory there's nothing to copy from and the volume's
# mount point defaults to root:root instead - confirmed by an actual `docker compose` run, where the
# key ring failed with Permission denied on every restart despite the chown below. Creating the
# directory here first, before the volume can seed itself from an ownerless path, fixes that.
RUN mkdir -p /app/DataProtection-Keys && chown -R app:app /app
USER app

# .NET 8's Linux container images default Kestrel to port 8080 for the non-root user (ports <1024
# need root to bind). Match that here so `docker run -p host:8080` works without extra config.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# No appsettings.json is copied in - it's gitignored because it holds real secrets (DB password, JWT
# signing key). Supply configuration via environment variables at `docker run`/compose time instead,
# using ASP.NET Core's double-underscore convention for nested keys, e.g.:
#   ConnectionStrings__DefaultConnection, Jwt__SigningKey, Llm__Gemini__ApiKey
# See README.md's Configuration table for the full key list.
ENTRYPOINT ["dotnet", "AzureBuddy.Api.dll"]
