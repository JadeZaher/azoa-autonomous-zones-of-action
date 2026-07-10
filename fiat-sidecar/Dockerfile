FROM node:20-alpine AS builder

WORKDIR /app
# We need to copy sdk as well since fiat-sidecar depends on it locally
COPY sdk/azoa-wallet /app/sdk/azoa-wallet
COPY fiat-sidecar /app/fiat-sidecar

WORKDIR /app/sdk/azoa-wallet
RUN npm install
RUN npm run build

WORKDIR /app/fiat-sidecar
RUN npm install
RUN npm run build

FROM node:20-alpine

WORKDIR /app
COPY --from=builder /app/sdk/azoa-wallet /app/sdk/azoa-wallet
COPY --from=builder /app/fiat-sidecar /app/fiat-sidecar

WORKDIR /app/fiat-sidecar
EXPOSE 4000

CMD ["npm", "start"]
