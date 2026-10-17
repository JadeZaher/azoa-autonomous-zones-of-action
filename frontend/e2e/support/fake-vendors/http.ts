import { readFileSync } from 'node:fs'
import { createServer, type Server } from 'node:https'
import type { IncomingMessage, ServerResponse } from 'node:http'
import path from 'node:path'

export interface VendorRequest {
  method: string
  path: string
  query: URLSearchParams
  headers: IncomingMessage['headers']
  body: string
}

export interface VendorResponse {
  status: number
  body?: unknown
  html?: string
}

export type VendorHandler = (request: VendorRequest) => VendorResponse | Promise<VendorResponse>

/** JSON (or HTML) response helper shared by the fakes. */
export function respond(status: number, body?: unknown): VendorResponse {
  return { status, body }
}

/**
 * One HTTPS listener on 0.0.0.0 (the API container dials it through the host gateway) using the
 * per-run test certificate from `.generated/`. See `e2e/AGENTS.md` §fake-vendors.
 */
export async function listenHttps(port: number, generatedDirectory: string, handler: VendorHandler): Promise<Server> {
  const server = createServer(
    {
      cert: readFileSync(path.join(generatedDirectory, 'vendor.pem')),
      key: readFileSync(path.join(generatedDirectory, 'vendor-key.pem')),
    },
    (incoming, outgoing) => void dispatch(incoming, outgoing, handler),
  )
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject)
    server.listen(port, '0.0.0.0', () => resolve())
  })
  return server
}

async function dispatch(incoming: IncomingMessage, outgoing: ServerResponse, handler: VendorHandler): Promise<void> {
  const body = await new Promise<string>((resolve, reject) => {
    const chunks: Buffer[] = []
    incoming.on('data', (chunk: Buffer) => chunks.push(chunk))
    incoming.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')))
    incoming.on('error', reject)
  })
  const url = new URL(incoming.url ?? '/', 'https://vendor.invalid')
  try {
    const result = await handler({
      method: incoming.method ?? 'GET',
      path: url.pathname,
      query: url.searchParams,
      headers: incoming.headers,
      body,
    })
    if (result.html !== undefined) {
      outgoing.writeHead(result.status, { 'Content-Type': 'text/html; charset=utf-8' })
      outgoing.end(result.html)
      return
    }
    outgoing.writeHead(result.status, { 'Content-Type': 'application/json' })
    outgoing.end(result.body === undefined ? '' : JSON.stringify(result.body))
  } catch (error) {
    outgoing.writeHead(500, { 'Content-Type': 'application/json' })
    outgoing.end(JSON.stringify({ error: error instanceof Error ? error.message : 'fake vendor failure' }))
  }
}

export function closeServer(server: Server): Promise<void> {
  return new Promise((resolve) => server.close(() => resolve()))
}
