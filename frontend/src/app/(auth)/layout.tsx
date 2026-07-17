import Link from 'next/link'
import { HeroDag } from '@/components/landing/hero-dag'

export default function AuthLayout({
  children,
}: {
  children: React.ReactNode
}) {
  return (
    <div className="relative grid min-h-screen lg:grid-cols-2">
      {/* Left panel — AZOA brand side with the live quest-graph animation */}
      <div className="relative hidden flex-col justify-between overflow-hidden bg-[#16120D] p-10 text-[#F2EDE3] lg:flex">
        <HeroDag ink="#F2EDE3" accent="#C8501E" speed={0.8} density={0.9} />
        <div
          className="pointer-events-none absolute inset-0"
          style={{
            background:
              'linear-gradient(to top, rgba(22,18,13,0.9) 0%, rgba(22,18,13,0.45) 50%, rgba(22,18,13,0.75) 100%)',
          }}
        />

        {/* Wordmark + live badge — wordmark returns to the marketing landing */}
        <div className="relative z-10 flex items-start justify-between">
          <Link href="/" className="group -m-2 block p-2 transition-opacity hover:opacity-80">
            <div className="text-2xl font-bold tracking-[0.08em]">AZOA</div>
            <div className="mt-2 font-mono text-[11px] tracking-[0.18em] text-[#C8501E]">
              AUTONOMOUS ZONES OF ACTION
            </div>
          </Link>
          <div className="flex items-center gap-2 font-mono text-[11px] tracking-[0.1em] text-[#b7ad9c]">
            <span className="inline-block h-2 w-2 rounded-full bg-[#C8501E]" />
            LIVE — QUEST GRAPH
          </div>
        </div>

        {/* Statement + quote */}
        <div className="relative z-10 space-y-8">
          <h2 className="max-w-md text-4xl font-bold leading-[0.98] tracking-[-0.02em]">
            One avatar.
            <br />
            Every economy.
          </h2>
          <blockquote className="space-y-3">
            <p className="max-w-md text-lg leading-relaxed text-[#F2EDE3]/80">
              &ldquo;Your sovereign digital identity, unified across every
              chain. One avatar, infinite possibilities.&rdquo;
            </p>
            <footer className="font-mono text-[11px] tracking-[0.12em] text-[#b7ad9c]">
              — THE AZOA PROTOCOL
            </footer>
          </blockquote>
        </div>
      </div>

      {/* Right panel — form */}
      <div className="flex items-center justify-center bg-background p-4 sm:p-8">
        {/* Mobile wordmark */}
        <div className="absolute left-6 top-6 lg:hidden">
          <Link href="/" className="flex items-center gap-2.5">
            <div className="flex h-9 w-9 items-center justify-center rounded-sm bg-[#16120D]">
              <span className="text-sm font-bold text-[#F2EDE3]">A</span>
            </div>
            <span className="text-lg font-bold tracking-[0.06em]">AZOA</span>
          </Link>
        </div>

        <div className="w-full max-w-sm">{children}</div>
      </div>
    </div>
  )
}
