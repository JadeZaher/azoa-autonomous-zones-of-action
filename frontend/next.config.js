const path = require('path')

/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  swcMinify: true,
  // Pre-existing TypeScript noise in the frontend tree is tracked outside
  // the build gate (see the dev's [[no-frontend-typecheck]] memory). `next
  // build` would otherwise fail compilation on those errors; CI runs SDK
  // typecheck + dotnet build instead. Override via NEXT_TS_STRICT=1 when
  // you actively want type errors surfaced during a build.
  typescript: {
    ignoreBuildErrors: process.env.NEXT_TS_STRICT !== '1',
  },
  eslint: {
    // Same reasoning: ESLint findings should not gate the docker image build.
    ignoreDuringBuilds: true,
  },
  // NOTE: no NEXT_PUBLIC_API_URL here on purpose — that bakes the API URL at
  // `next build` time, which is the bug this file fixes. The API URL is
  // resolved at request time instead (see src/lib/runtime-config.ts).
  images: {
    domains: ['localhost'],
  },
  // Transpile the local SDK package for Next.js bundling
  transpilePackages: ['@azoa/sdk'],
  webpack: (config) => {
    // Ensure the SDK's dependencies resolve from the frontend's node_modules
    // so that @noble/curves/ed25519 subpath imports work correctly
    config.resolve.modules = [
      path.resolve(__dirname, 'node_modules'),
      ...(config.resolve.modules || ['node_modules']),
    ]
    // Fallback for Node.js built-ins used by crypto libs
    config.resolve.fallback = {
      ...config.resolve.fallback,
      crypto: false,
      stream: false,
      buffer: false,
    }
    return config
  },
}

module.exports = nextConfig