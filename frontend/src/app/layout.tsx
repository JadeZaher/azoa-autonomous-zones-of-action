import type { Metadata } from 'next'
import { Inter } from 'next/font/google'
import { NetworkProvider } from '@/lib/network-context'
import { DebugProvider } from '@/lib/debug-context'
import { OasisProvider } from '@/lib/oasis-context'
import { TooltipProvider } from '@/components/ui/tooltip'
import './globals.css'

const inter = Inter({ subsets: ['latin'], variable: '--font-sans' })

export const metadata: Metadata = {
  title: 'OASIS Sleek',
  description: 'Avatar NFT & Blockchain Platform',
}

export default function RootLayout({
  children,
}: {
  children: React.ReactNode
}) {
  return (
    <html lang="en" className="dark">
      <body className={`${inter.variable} font-sans antialiased`}>
        <NetworkProvider>
          <DebugProvider>
            <OasisProvider>
              <TooltipProvider>{children}</TooltipProvider>
            </OasisProvider>
          </DebugProvider>
        </NetworkProvider>
      </body>
    </html>
  )
}
