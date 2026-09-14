# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Project files first, restore, then the rest of the source. Restore is the slow layer and
# it only depends on the package lists - copying everything up front would re-download the
# world on every source edit.
COPY global.json Directory.Build.props ./
COPY src/SlotLock.Domain/*.csproj          src/SlotLock.Domain/
COPY src/SlotLock.Application/*.csproj     src/SlotLock.Application/
COPY src/SlotLock.Infrastructure/*.csproj  src/SlotLock.Infrastructure/
COPY src/SlotLock.Api/*.csproj             src/SlotLock.Api/
RUN dotnet restore src/SlotLock.Api/SlotLock.Api.csproj

COPY src/ src/
RUN dotnet publish src/SlotLock.Api/SlotLock.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app

# ---------------------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# This service reasons about opening hours in a resource's own zone, so it needs the IANA
# time zone database at run time. The Debian-based runtime image carries ICU but not always
# tzdata, and the failure mode is a TimeZoneNotFoundException for "Asia/Amman" at the moment
# a slot is generated - long after the image was built and pushed.
#
# It is also why the Alpine image is not used here: it ships without ICU, and globalisation
# would have to be disabled, which breaks the same lookups.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends tzdata \
    && rm --recursive --force /var/lib/apt/lists/*

# Non-root. Nothing here needs to write to the filesystem, and a container that cannot
# escalate is one less thing to reason about if the process is ever compromised.
RUN useradd --create-home --shell /usr/sbin/nologin slotlock
USER slotlock

COPY --from=build --chown=slotlock:slotlock /app ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

# Readiness, not liveness: the orchestrator should stop routing traffic to an instance that
# cannot reach the database, rather than restarting it into the same failure.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/bin/sh", "-c", "curl --fail --silent http://localhost:8080/health/ready || exit 1"]

ENTRYPOINT ["dotnet", "SlotLock.Api.dll"]
