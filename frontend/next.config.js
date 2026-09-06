const path = require('path')

/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  turbopack: {
    // Repo root, not `frontend/`. `azoa-sdk` is a path dependency symlinked to
    // the sibling `sdk/azoa-wallet`, and Turbopack cannot resolve a link that
    // escapes its root — pinning the root to __dirname makes every `azoa-sdk`
    // import fail to resolve at build time (tsc and vitest resolve it fine, so
    // the failure only appears in `next build`).
    root: path.join(__dirname, '..'),
  },
  // NOTE: no NEXT_PUBLIC_API_URL here on purpose — that bakes the API URL at
  // `next build` time, which is the bug this file fixes. The API URL is
  // resolved at request time instead (see src/lib/runtime-config.ts).
  images: {
    remotePatterns:
      process.env.NODE_ENV === 'production'
        ? []
        : [
            {
              protocol: 'http',
              hostname: 'localhost',
            },
          ],
  },
  // Transpile the published SDK package for Next.js bundling
  transpilePackages: ['azoa-sdk'],
  async headers() {
    return [
      {
        source: '/(.*)',
        headers: [
          { key: 'X-Content-Type-Options', value: 'nosniff' },
          { key: 'X-Frame-Options', value: 'DENY' },
          { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
          {
            key: 'Permissions-Policy',
            value: 'camera=(), microphone=(), geolocation=(), payment=(), usb=()',
          },
        ],
      },
    ]
  },
}

module.exports = nextConfig
