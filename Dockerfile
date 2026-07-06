# ─── AZOA .NET WebAPI ───
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# unzip is needed to extract the SurrealForge.Schema CLI payload from its nupkg.
RUN apt-get update && apt-get install -y --no-install-recommends unzip \
    && rm -rf /var/lib/apt/lists/*

# Copy everything and restore the WebAPI (which pulls the SurrealForge.*
# packages from NuGet). The container also needs the schema CLI
# (SurrealForge.Schema) so it can run `surrealforge up` as a pre-start
# migration step.
COPY . .
RUN dotnet restore AZOA.WebAPI.csproj

RUN dotnet publish AZOA.WebAPI.csproj -c Release -o /app/publish --no-restore

# Stage the SurrealForge schema/migration CLI so the entrypoint can run
# `surrealforge up` at boot. The published SurrealForge.Schema package ships a
# complete framework-dependent CLI payload under tools/net10.0/any/ (DLL +
# runtimeconfig + deps), but the 0.1.x nuspec omits the DotnetTool package type,
# so `dotnet tool install` rejects it ("not a .NET tool"). We extract that
# tools/ payload directly instead — it runs as `dotnet SurrealForge.Schema.dll`.
ARG SURREALFORGE_SCHEMA_VERSION=0.1.1
RUN set -eu; \
    url="https://api.nuget.org/v3-flatcontainer/surrealforge.schema/${SURREALFORGE_SCHEMA_VERSION}/surrealforge.schema.${SURREALFORGE_SCHEMA_VERSION}.nupkg"; \
    curl -fsSL "$url" -o /tmp/sfs.nupkg; \
    mkdir -p /app/schema-cli; \
    cd /app/schema-cli; \
    unzip -q -o /tmp/sfs.nupkg "tools/net10.0/any/*" -d /tmp/sfs; \
    cp -r /tmp/sfs/tools/net10.0/any/* /app/schema-cli/; \
    rm -rf /tmp/sfs /tmp/sfs.nupkg; \
    test -f /app/schema-cli/SurrealForge.Schema.dll

# Also stage the committed schemas + migrations folder into the image so
# the runtime container can apply them at boot via the schema CLI.
RUN mkdir -p /app/persistence && cp -r Persistence/SurrealDb /app/persistence/SurrealDb

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is needed for the entrypoint's pre-flight health probe against
# SurrealDB before we run migrations. Tiny additional install on top of
# the base aspnet image.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY --from=build /app/schema-cli ./schema-cli/
COPY --from=build /app/persistence ./persistence/
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# Run as the non-root user the aspnet base image ships (APP_UID=1654).
# /app must be owned by that user so any runtime-written files (e.g. the
# dev JSONL exception log dir) succeed without root.
RUN chown -R $APP_UID /app
USER $APP_UID

# ASPNETCORE_URLS is set by docker-entrypoint.sh so Railway's injected $PORT
# is honored (defaults to 5000 for compose). Do NOT bake it here, or the
# entrypoint's ${ASPNETCORE_URLS:-...} fallback can never see $PORT.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5000

# The entrypoint waits for SurrealDB, runs `surrealforge up`, then execs
# the WebAPI host. Set AZOA_SKIP_MIGRATIONS=1 to bypass the migration
# step when running against a DB that has already been migrated.
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
