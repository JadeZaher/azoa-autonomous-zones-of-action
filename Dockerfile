# ─── AZOA .NET WebAPI ───
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy everything and restore the WebAPI (which pulls the SurrealForge.*
# packages from NuGet). The container also needs the schema CLI
# (SurrealForge.Schema) so it can run `surrealforge up` as a pre-start
# migration step.
COPY . .
RUN dotnet restore AZOA.WebAPI.csproj

ARG SOURCE_REVISION
ARG RAILWAY_GIT_COMMIT_SHA
RUN source_revision="${SOURCE_REVISION:-$RAILWAY_GIT_COMMIT_SHA}"; \
    case "$source_revision" in \
      [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]) ;; \
      *) echo "SOURCE_REVISION must be a 40-character lowercase Git SHA." >&2; exit 1 ;; \
    esac \
    && dotnet publish AZOA.WebAPI.csproj -c Release -o /app/publish --no-restore -p:SourceRevisionId="$source_revision"

# Stage the schema CLI. As of 1.2.0 the CLI is its own package
# (SurrealForge.Cli, a real DotnetTool) and SurrealForge.Schema is a plain
# library with no tools/ payload, so the old "copy out of the NuGet cache"
# step has nothing to copy.
#
# The version is read from the SurrealForge.Schema PackageReference rather
# than hard-coded: the suite publishes in lockstep, and a hard-coded copy
# here is exactly what silently drifted from the restored package and broke
# the image build.
RUN set -eu; \
    version="$(sed -n 's/.*Include="SurrealForge\.Schema"[[:space:]]*Version="\([^"]*\)".*/\1/p' AZOA.WebAPI.csproj)"; \
    test -n "$version"; \
    echo "Schema CLI version: $version"; \
    dotnet tool install --tool-path /app/schema-cli SurrealForge.Cli --version "$version"; \
    test -x /app/schema-cli/surrealforge

# Also stage the committed schemas + migrations folder into the image so
# the runtime container can apply them at boot via the schema CLI.
RUN mkdir -p /app/persistence && cp -r Persistence/SurrealDb /app/persistence/SurrealDb

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is needed for the entrypoint's pre-flight health probe against
# SurrealDB before we run migrations. Tiny additional install on top of
# the base aspnet image.
RUN apt-get update && apt-get install -y --no-install-recommends curl util-linux \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY --from=build /app/schema-cli ./schema-cli/
COPY --from=build /app/persistence ./persistence/
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# Run as the non-root user the aspnet base image ships (APP_UID=1654).
# /app must be owned by that user so any runtime-written files (e.g. the
# dev JSONL exception log dir) succeed without root. Railway temporarily starts
# the entrypoint as root only to repair its root-owned volume, then setpriv drops
# back to this uid before the API host starts.
RUN mkdir -p /app/data/data-protection-keys && chown -R $APP_UID /app
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
