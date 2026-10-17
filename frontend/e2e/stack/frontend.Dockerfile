# ─── AZOA frontend image for the E2E stack only ───
#
# frontend/package.json depends on `azoa-sdk` as `file:../sdk/azoa-wallet`, and the repo
# .dockerignore excludes every `dist/`, so the SDK is built here before the frontend installs.
# The /repo/{sdk,frontend} layout matches next.config.js (Turbopack root = the repo root).
# Build context: the repository root (see docker-compose.e2e.yml). See frontend/e2e/AGENTS.md §stack.

FROM node:20-alpine

WORKDIR /repo/sdk/azoa-wallet
COPY sdk/azoa-wallet/package.json sdk/azoa-wallet/package-lock.json ./
RUN npm ci
COPY sdk/azoa-wallet/ ./
RUN npm run build

WORKDIR /repo/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
# Explicit paths: never frontend/e2e/ (its .generated/ holds the run's throwaway CA key).
COPY frontend/next.config.js frontend/tsconfig.json frontend/postcss.config.js frontend/tailwind.config.js frontend/components.json frontend/next-env.d.ts ./
COPY frontend/public ./public
COPY frontend/src ./src
RUN npm run build

ENV NODE_ENV=production
EXPOSE 3000
CMD ["npm", "run", "start"]
