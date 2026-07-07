# SurrealDB v3.1.4 for Railway — wraps the official image with a start command
# that persists to the mounted /data volume.
#
# The official surrealdb/surrealdb image is DISTROLESS (no /bin/sh), so CMD must
# be exec-form (JSON array) — a shell-form CMD would try /bin/sh and fail to
# boot. The image entrypoint is `/surreal`, so CMD supplies only the args.
#
# Root creds are read by SurrealDB from the SURREAL_USER / SURREAL_PASS env vars
# set on the Railway service (so they are not baked into the image). Binds a
# fixed internal port 8000 — SurrealDB is a private backing service reached only
# at `surrealdb.railway.internal:8000`, with no public HTTP surface.
FROM surrealdb/surrealdb:v3.1.4

EXPOSE 8000

CMD ["start", "--log", "info", "--bind", "0.0.0.0:8000", "rocksdb:/data/azoa.db"]
