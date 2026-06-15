# SurrealDB service image for Railway (and any container host).
#
# Wraps the official v3.1.4 image so the DB ships from the same repo push as
# the api and frontend services. The official image is distroless (no shell),
# so configuration is via env vars that the `surreal` binary reads directly —
# not a shell entrypoint:
#   SURREAL_USER / SURREAL_PASS  — initial root user (set as Railway secrets)
#   SURREAL_BIND                 — listen address (default 0.0.0.0:8000)
#   SURREAL_PATH                 — storage URI (default rocksdb:///data/db)
#
# Railway note: set the service's target port to 8000 (SurrealDB does not read
# Railway's injected $PORT — it has no shell to expand it — so bind a fixed
# 8000 and point the service port at it). Mount a volume at /data.
FROM surrealdb/surrealdb:v3.1.4

# Run as root so the service can create the RocksDB directory inside the
# mounted volume (the image's default `nonroot` user cannot).
USER 0:0

ENV SURREAL_BIND=0.0.0.0:8000 \
    SURREAL_PATH=rocksdb:///data/db

EXPOSE 8000
ENTRYPOINT ["/surreal"]
CMD ["start"]
