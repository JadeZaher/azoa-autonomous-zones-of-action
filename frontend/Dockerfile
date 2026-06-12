# ─── OASIS Frontend (Next.js) ───

# Stage 1: Build the SDK
FROM node:20-alpine AS sdk-build
WORKDIR /sdk
COPY sdk/oasis-wallet/package.json sdk/oasis-wallet/tsconfig.json sdk/oasis-wallet/tsup.config.ts ./
RUN npm install
COPY sdk/oasis-wallet/src ./src
RUN npx tsup

# Stage 2: Build the frontend
FROM node:20-alpine AS frontend-build
WORKDIR /app

# Copy SDK build output (needed for the local file: link)
COPY --from=sdk-build /sdk /sdk

# Copy frontend package.json and install deps
COPY frontend/package.json ./
# Rewrite the local SDK path for container context
RUN sed -i 's|"file:../sdk/oasis-wallet"|"file:/sdk"|' package.json
RUN npm install

# Copy frontend source
COPY frontend/ .

# Build Next.js
ENV NEXT_PUBLIC_API_URL=http://localhost:5000
RUN npm run build

# Stage 3: Production runtime
FROM node:20-alpine AS runtime
WORKDIR /app

COPY --from=frontend-build /app/.next ./.next
COPY --from=frontend-build /app/node_modules ./node_modules
COPY --from=frontend-build /app/package.json ./
COPY --from=frontend-build /app/next.config.js ./
COPY --from=frontend-build /app/public ./public

ENV NODE_ENV=production
ENV NEXT_PUBLIC_API_URL=http://oasis-api:5000
EXPOSE 3000

CMD ["npm", "run", "start"]
