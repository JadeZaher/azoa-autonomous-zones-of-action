# ─── AZOA Frontend (Next.js) ───
# The SDK is built from source: frontend/package.json depends on
# "azoa-sdk": "file:../sdk/azoa-wallet" (commit 2333d248). The image mirrors the
# repo layout (/app/sdk/azoa-wallet beside /app/frontend) so that file: link —
# a symlink in node_modules — resolves at build AND run time exactly as it does
# locally. Build context must be the repo root (Railway: RAILWAY_DOCKERFILE_PATH).

# Stage 0: build the SDK
FROM node:20-alpine AS sdk-build
WORKDIR /app/sdk/azoa-wallet
COPY sdk/azoa-wallet/package.json sdk/azoa-wallet/package-lock.json ./
RUN npm ci
COPY sdk/azoa-wallet/ .
RUN npm run build

# Stage 1: build the frontend against the freshly built SDK
FROM node:20-alpine AS frontend-build
COPY --from=sdk-build /app/sdk/azoa-wallet /app/sdk/azoa-wallet
WORKDIR /app/frontend

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ .

# Build Next.js. API_URL is intentionally NOT set here: the API base URL is
# resolved at container-start/request time (see src/lib/runtime-config.ts +
# src/app/layout.tsx), not baked into the client bundle. Only non-secret,
# genuinely build-time NEXT_PUBLIC_* values belong in this stage.
RUN npm run build
RUN npm prune --omit=dev

# Stage 2: production runtime (same /app/sdk + /app/frontend layout so the
# node_modules/azoa-sdk symlink still resolves)
FROM node:20-alpine AS runtime
COPY --from=sdk-build /app/sdk/azoa-wallet /app/sdk/azoa-wallet
WORKDIR /app/frontend

COPY --from=frontend-build /app/frontend/.next ./.next
COPY --from=frontend-build /app/frontend/node_modules ./node_modules
COPY --from=frontend-build /app/frontend/package.json ./
COPY --from=frontend-build /app/frontend/next.config.js ./
COPY --from=frontend-build /app/frontend/public ./public

ENV NODE_ENV=production
# API_URL is read at request time (server-injected into window.__RUNTIME_CONFIG__)
# so an operator can repoint the UI by changing this env var and restarting the
# container — no image rebuild required. Production has no fallback; API_URL
# must be a public browser-reachable HTTPS origin.
EXPOSE 3000

CMD ["npm", "run", "start"]
