import { AlertCircle, CheckCircle2, CircleDashed, RefreshCw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { cn } from '@/lib/utils'

export function OperatorPageHeader({
  eyebrow,
  title,
  description,
  onRefresh,
  refreshing = false,
}: {
  eyebrow: string
  title: string
  description: string
  onRefresh?: () => void
  refreshing?: boolean
}) {
  return (
    <div className="flex flex-col gap-4 border-b border-border pb-5 sm:flex-row sm:items-end sm:justify-between">
      <div>
        <p className="font-mono text-[10px] uppercase tracking-[0.2em] text-primary">{eyebrow}</p>
        <h1 className="mt-2 text-2xl font-semibold tracking-tight sm:text-3xl">{title}</h1>
        <p className="mt-2 max-w-2xl text-sm leading-6 text-muted-foreground">{description}</p>
      </div>
      {onRefresh && (
        <Button variant="outline" className="min-h-11 rounded-none self-start" onClick={onRefresh} disabled={refreshing}>
          <RefreshCw className={cn('mr-2 h-4 w-4', refreshing && 'animate-spin')} aria-hidden="true" />
          Refresh snapshot
        </Button>
      )}
    </div>
  )
}

export function FlatCard({ className, ...props }: React.ComponentProps<typeof Card>) {
  return <Card className={cn('rounded-none border border-border ring-0', className)} {...props} />
}

export function MetricCard({ label, value, detail }: { label: string; value: number | string; detail: string }) {
  return (
    <FlatCard>
      <CardHeader className="pb-0">
        <CardTitle className="font-mono text-[10px] uppercase tracking-[0.16em] text-muted-foreground">{label}</CardTitle>
      </CardHeader>
      <CardContent>
        <p className="text-3xl font-semibold tabular-nums">{value}</p>
        <p className="mt-1 text-xs leading-5 text-muted-foreground">{detail}</p>
      </CardContent>
    </FlatCard>
  )
}

export type StatusTone = 'ready' | 'attention' | 'neutral'

export function StatusPill({ tone, children }: { tone: StatusTone; children: React.ReactNode }) {
  const Icon = tone === 'ready' ? CheckCircle2 : tone === 'attention' ? AlertCircle : CircleDashed
  return (
    <span className={cn(
      'inline-flex min-h-7 items-center gap-1.5 border px-2 font-mono text-[10px] uppercase tracking-[0.1em]',
      tone === 'ready' && 'border-emerald-700 bg-emerald-50 text-emerald-800 dark:bg-emerald-950/30 dark:text-emerald-300',
      tone === 'attention' && 'border-amber-700 bg-amber-50 text-amber-900 dark:bg-amber-950/30 dark:text-amber-200',
      tone === 'neutral' && 'border-border bg-muted text-muted-foreground',
    )}>
      <Icon className="h-3.5 w-3.5" aria-hidden="true" />
      {children}
    </span>
  )
}

export function OperatorError({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div role="alert" aria-live="assertive" className="border border-destructive bg-destructive/5 p-4 text-sm text-destructive">
      <div className="flex items-start gap-3">
        <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
        <p className="flex-1 leading-6">{message}</p>
        {onRetry && <Button variant="outline" className="min-h-11 rounded-none" onClick={onRetry}>Retry</Button>}
      </div>
    </div>
  )
}

export function OperatorLoading({ label = 'Loading operator data' }: { label?: string }) {
  return (
    <div role="status" className="flex min-h-48 items-center justify-center border border-dashed border-border text-sm text-muted-foreground">
      <RefreshCw className="mr-2 h-4 w-4 animate-spin" aria-hidden="true" />
      {label}
    </div>
  )
}

export function formatOperatorDate(value?: string | null): string {
  if (!value) return 'Not recorded'
  const date = new Date(value)
  return Number.isFinite(date.getTime())
    ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date)
    : 'Invalid timestamp'
}
