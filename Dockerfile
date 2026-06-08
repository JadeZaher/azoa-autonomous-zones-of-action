# ─── OASIS .NET WebAPI ───
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy everything and let the SDK's solution-walk handle restore. We need
# the schema CLI (Oasis.SurrealDb.Schema) alongside the WebAPI so the
# container can run `oasis-surreal up` as a pre-start migration step.
COPY . .
RUN dotnet restore OASIS.WebAPI.csproj
RUN dotnet restore packages/Oasis.SurrealDb.Schema/Oasis.SurrealDb.Schema.csproj

RUN dotnet publish OASIS.WebAPI.csproj -c Release -o /app/publish --no-restore

# The Schema package sets <IsPublishable>false</IsPublishable> because it
# ships as a dotnet pack tool, not as an app. Override those two properties
# from the CLI so `dotnet publish` actually emits a ready-to-run binary
# (otherwise the publish step is a silent no-op).
RUN dotnet publish packages/Oasis.SurrealDb.Schema/Oasis.SurrealDb.Schema.csproj \
    -c Release -o /app/schema-cli --no-restore --framework net8.0 \
    -p:IsPublishable=true -p:PackAsTool=false

# Also stage the committed schemas + migrations folder into the image so
# the runtime container can apply them at boot via the schema CLI.
RUN mkdir -p /app/persistence && cp -r Persistence/SurrealDb /app/persistence/SurrealDb

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
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

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 5000

# The entrypoint waits for SurrealDB, runs `oasis-surreal up`, then execs
# the WebAPI host. Set OASIS_SKIP_MIGRATIONS=1 to bypass the migration
# step when running against a DB that has already been migrated.
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
