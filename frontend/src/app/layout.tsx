import type { Metadata } from 'next'
import { Space_Grotesk, IBM_Plex_Mono } from 'next/font/google'
import { NetworkProvider } from '@/lib/network-context'
import { DebugProvider } from '@/lib/debug-context'
import { AzoaProvider } from '@/lib/azoa-context'
import { TooltipProvider } from '@/components/ui/tooltip'
import { resolveServerApiUrl } from '@/lib/runtime-config'
import './globals.css'

const spaceGrotesk = Space_Grotesk({
  subsets: ['latin'],
  weight: ['400', '500', '700'],
  variable: '--font-sans',
})
const plexMono = IBM_Plex_Mono({
  subsets: ['latin'],
  weight: ['400', '500'],
  variable: '--font-mono',
})

export const metadata: Metadata = {
  title: 'AZOA — Autonomous Zones of Action',
  description: 'One engine. Every kind of economy.',
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
