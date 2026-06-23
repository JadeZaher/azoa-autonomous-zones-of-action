import type { Metadata } from 'next'
import { Inter } from 'next/font/google'
import { NetworkProvider } from '@/lib/network-context'
import { DebugProvider } from '@/lib/debug-context'
import { AzoaProvider } from '@/lib/azoa-context'
import { TooltipProvider } from '@/components/ui/tooltip'
import './globals.css'

const inter = Inter({ subsets: ['latin'], variable: '--font-sans' })

export const metadata: Metadata = {
  title: 'AZOA Sleek',
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
            <AzoaProvider>
              <TooltipProvider>{children}</TooltipProvider>
            </AzoaProvider>
          </DebugProvider>
        </NetworkProvider>
      </body>
    </html>
  )
}
