import type { Metadata } from 'next'
import localFont from 'next/font/local'
import { NetworkProvider } from '@/lib/network-context'
import { DebugProvider } from '@/lib/debug-context'
import { AzoaProvider } from '@/lib/azoa-context'
import { TooltipProvider } from '@/components/ui/tooltip'
import { SISTER_SITES } from '@/components/landing/sisters'
import { resolveServerApiUrl } from '@/lib/runtime-config'
import './globals.css'

const organization = {
  '@context': 'https://schema.org',
  '@type': 'Organization',
  name: 'AZOA',
  url: 'https://azoa.ardanova.com',
  sameAs: SISTER_SITES.map((site) => site.href),
}

// Self-hosted latin subsets: `next build` makes no network call (see frontend/src/app/fonts/AGENTS.md).
const spaceGrotesk = localFont({
  src: './fonts/space-grotesk-latin-variable.woff2',
  weight: '300 700',
  display: 'swap',
  variable: '--font-sans',
})
const plexMono = localFont({
  src: [
    { path: './fonts/ibm-plex-mono-latin-400.woff2', weight: '400', style: 'normal' },
    { path: './fonts/ibm-plex-mono-latin-500.woff2', weight: '500', style: 'normal' },
  ],
  display: 'swap',
  adjustFontFallback: false,
  variable: '--font-mono',
})

const SHARE_IMAGE = 'https://azoa.ardanova.com/og/azoa-og-1200x630.png'

export const metadata: Metadata = {
  metadataBase: new URL('https://azoa.ardanova.com'),
  title: 'AZOA — Autonomous Zones of Action',
  description: 'Open-source engine for structured workflows. This hosted site is a simulation: real-value bridging is disabled, and fiat settlement is not enabled yet.',
  icons: {
    icon: [
      { url: '/favicons/azoa-favicon-32.png', sizes: '32x32', type: 'image/png' },
      { url: '/favicons/azoa-favicon-48.png', sizes: '48x48', type: 'image/png' },
    ],
    apple: [{ url: '/favicons/azoa-favicon-180.png', sizes: '180x180', type: 'image/png' }],
  },
  openGraph: {
    images: [{ url: SHARE_IMAGE, width: 1200, height: 630, alt: 'AZOA — Autonomous Zones of Action' }],
  },
  twitter: {
    card: 'summary_large_image',
    images: [SHARE_IMAGE],
  },
}

export const viewport = {
  width: 'device-width',
  initialScale: 1,
}

// Force per-request rendering so resolveServerApiUrl() reads the live
// process.env.API_URL at request time. Without this Next.js statically
// pre-renders the layout at build time (when API_URL is unset), baking the
// localhost fallback into window.__RUNTIME_CONFIG__ — the runtime-config seam
// only works if the layout is dynamic. See src/lib/runtime-config.ts.
export const dynamic = 'force-dynamic'

// Server Component: re-reads process.env on every request, so this is the
// seam that makes the API URL runtime-resolvable (see lib/runtime-config.ts).
export default function RootLayout({
  children,
}: {
  children: React.ReactNode
}) {
  const runtimeConfig = { apiUrl: resolveServerApiUrl() }
  // Escape script-breaking chars so an env-sourced value can't close the tag.
  const serializedConfig = JSON.stringify(runtimeConfig)
    .replace(/</g, '\\u003c')
    .replace(/\u2028/g, '\\u2028')
    .replace(/\u2029/g, '\\u2029')
  return (
    <html lang="en" className="dark">
      <body className={`${spaceGrotesk.variable} ${plexMono.variable} font-sans antialiased`}>
        <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(organization) }} />
        {/* Injected at request time — NOT a build-time constant. */}
        <script
          id="__RUNTIME_CONFIG__"
          dangerouslySetInnerHTML={{
            __html: `window.__RUNTIME_CONFIG__=${serializedConfig};`,
          }}
        />
        <NetworkProvider>
          <DebugProvider>
            <AzoaProvider>
              <TooltipProvider>{children}</TooltipProvider>
            </AzoaProvider>
          </DebugProvider>
        </NetworkProvider>
      </body>
    </html>
  )
}
