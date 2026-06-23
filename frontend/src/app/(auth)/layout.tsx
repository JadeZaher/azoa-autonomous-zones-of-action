export default function AuthLayout({
  children,
}: {
  children: React.ReactNode
}) {
  return (
    <div className="relative grid min-h-screen lg:grid-cols-2">
      {/* Left panel — decorative branding side */}
      <div className="relative hidden flex-col justify-between overflow-hidden bg-zinc-900 p-10 lg:flex">
        {/* Animated gradient mesh */}
        <div className="absolute inset-0 bg-gradient-to-br from-zinc-900 via-zinc-900/90 to-blue-950" />
        <div className="absolute inset-0 bg-[radial-gradient(ellipse_at_top_right,_var(--tw-gradient-stops))] from-blue-700/20 via-transparent to-transparent" />
        <div className="absolute inset-0 bg-[radial-gradient(ellipse_at_bottom_left,_var(--tw-gradient-stops))] from-indigo-600/10 via-transparent to-transparent" />

        {/* Logo */}
        <div className="relative z-10">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-white shadow-lg shadow-white/10">
              <svg
                viewBox="0 0 24 24"
                className="h-6 w-6 text-zinc-900"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                strokeLinecap="round"
                strokeLinejoin="round"
              >
                <path d="M12 2L2 7l10 5 10-5-10-5z" />
                <path d="M2 17l10 5 10-5" />
                <path d="M2 12l10 5 10-5" />
              </svg>
            </div>
            <span className="text-xl font-bold tracking-tight text-white">AZOA</span>
          </div>
        </div>

        {/* Quote */}
        <div className="relative z-10">
          <blockquote className="space-y-3">
            <p className="text-lg leading-relaxed text-white/80">
              &ldquo;Your sovereign digital identity, unified across every
              chain. One avatar, infinite possibilities.&rdquo;
            </p>
            <footer className="text-sm text-white/50">
              — The AZOA Protocol
            </footer>
          </blockquote>
        </div>
      </div>

      {/* Right panel — form */}
      <div className="flex items-center justify-center bg-background p-4 sm:p-8">
        {/* Mobile logo */}
        <div className="absolute left-6 top-6 lg:hidden">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary shadow-md shadow-primary/20">
            <svg
              viewBox="0 0 24 24"
              className="h-5 w-5 text-primary-foreground"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              <path d="M12 2L2 7l10 5 10-5-10-5z" />
            </svg>
          </div>
        </div>

        <div className="w-full max-w-sm">{children}</div>
      </div>
    </div>
  )
}
