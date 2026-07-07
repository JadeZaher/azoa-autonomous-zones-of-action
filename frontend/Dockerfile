# ─── AZOA Frontend (Next.js) ───
# The @azoa SDK is consumed from npm as `azoa-sdk` (see frontend/package.json),
# so there is no local SDK build step — `npm install` pulls the published
# package. Build context can be the repo root or frontend/; only frontend/ is used.

# Stage 1: Build the frontend
FROM node:20-alpine AS frontend-build
WORKDIR /app

COPY frontend/package.json ./
RUN npm install

COPY frontend/ .

# Build Next.js. API_URL is intentionally NOT set here: the API base URL is
# resolved at container-start/request time (see src/lib/runtime-config.ts +
# src/app/layout.tsx), not baked into the client bundle. Only non-secret,
# genuinely build-time NEXT_PUBLIC_* values belong in this stage.
RUN npm run build

# Stage 2: Production runtime
FROM node:20-alpine AS runtime
WORKDIR /app

COPY --from=frontend-build /app/.next ./.next
COPY --from=frontend-build /app/node_modules ./node_modules
COPY --from=frontend-build /app/package.json ./
COPY --from=frontend-build /app/next.config.js ./
COPY --from=frontend-build /app/public ./public

ENV NODE_ENV=production
# API_URL is read at request time (server-injected into window.__RUNTIME_CONFIG__)
# so an operator can repoint the UI by changing this env var and restarting the
# container — no image rebuild required. Default matches the compose network name.
ENV API_URL=http://azoa-api:5000
EXPOSE 3000

CMD ["npm", "run", "start"]
