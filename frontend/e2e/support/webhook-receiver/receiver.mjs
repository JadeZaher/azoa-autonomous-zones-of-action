// E2E webhook receiver: records every delivery the API posts and answers 2xx; see frontend/e2e/AGENTS.md §webhook-receiver.
// Runs as the `webhook-receiver` compose service at the pinned public-range address the SSRF guard admits.
import { readFileSync } from 'node:fs'
import { createServer } from 'node:https'

const certificates = process.env.RECEIVER_CERT_DIR ?? '/certs'
const port = Number(process.env.RECEIVER_PORT ?? 8443)
const deliveries = []

const server = createServer(
  { cert: readFileSync(`${certificates}/vendor.pem`), key: readFileSync(`${certificates}/vendor-key.pem`) },
  (request, response) => {
    const chunks = []
    request.on('data', (chunk) => chunks.push(chunk))
    request.on('end', () => {
      const url = new URL(request.url ?? '/', 'https://receiver.invalid')
      const reply = (status, body) => {
        response.writeHead(status, { 'Content-Type': 'application/json' })
        response.end(JSON.stringify(body))
      }
      // Test-runner control plane, reached through the published loopback port only.
      if (url.pathname === '/__e2e/deliveries') {
        if (request.method === 'GET') return reply(200, deliveries)
        if (request.method === 'DELETE') return reply(200, { cleared: deliveries.splice(0).length })
        return reply(405, {})
      }
      if (request.method !== 'POST') return reply(405, {})
      deliveries.push({
        path: url.pathname,
        headers: Object.fromEntries(Object.entries(request.headers).filter(([name]) => name.startsWith('x-azoa-') || name === 'content-type')),
        body: Buffer.concat(chunks).toString('utf8'),
        receivedAt: new Date().toISOString(),
      })
      reply(200, { received: true })
    })
  },
)

server.listen(port, '0.0.0.0', () => console.log(`webhook receiver listening on ${port}`))
for (const signal of ['SIGTERM', 'SIGINT']) process.once(signal, () => server.close(() => process.exit(0)))
