# ─── AZOA .NET WebAPI ───
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy everything and restore the WebAPI (which pulls the SurrealForge.*
# packages from NuGet). The container also needs the schema CLI
# (SurrealForge.Schema) so it can run `surrealforge up` as a pre-start
# migration step.
COPY . .
RUN dotnet restore AZOA.WebAPI.csproj

RUN dotnet publish AZOA.WebAPI.csproj -c Release -o /app/publish --no-restore

# Install the SurrealForge schema/migration CLI (published as a dotnet tool
# on NuGet) into a self-contained path we copy into the runtime image.
RUN dotnet tool install --tool-path /app/schema-cli SurrealForge.Schema --version 0.1.1

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
