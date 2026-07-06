# SurrealDB v3.1.4 for Railway — wraps the official image with a start command
# that persists to the mounted /data volume. Root creds come from
# SURREAL_USER / SURREAL_PASS env vars (set on the service).
#
# Binds a FIXED internal port 8000 (not Railway's $PORT): SurrealDB is a private
# backing service reached only over Railway's private network at
# `surrealdb.railway.internal:8000` by the backend — it has no public HTTP
# surface, so it does not need to answer on the platform-assigned $PORT.
FROM surrealdb/surrealdb:v3.1.4

EXPOSE 8000

# Official image entrypoint is `/surreal`; CMD supplies the args. Shell form so
# $SURREAL_USER / $SURREAL_PASS expand at runtime. RocksDB on the /data volume.
CMD start --log info --bind 0.0.0.0:8000 \
    --user "${SURREAL_USER:-root}" --pass "${SURREAL_PASS:-root}" \
    rocksdb:/data/azoa.db
