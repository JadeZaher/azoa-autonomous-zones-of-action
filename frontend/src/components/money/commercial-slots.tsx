'use client'

import type { MoneySlots } from 'azoa-sdk/react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

/** Shared Tailwind + AZOA UI slot adapters for commercial and ride-along SDK components. */
export const commercialSlots: MoneySlots = {
  Button: ({ variant, type = 'button', ...props }) => (
    <Button
      {...props}
      type={type}
      size="sm"
      variant={variant === 'secondary' ? 'outline' : 'default'}
    />
  ),
  Input: ({ className = '', ...props }) => <Input {...props} className={className} />,
  Card: ({ title, description, children, className = '' }) => (
    <section
      className={
        'space-y-4 rounded-lg border bg-card p-5 text-card-foreground shadow-sm ' +
        '[&_form]:grid [&_form]:gap-3 sm:[&_form]:grid-cols-2 [&_form>button]:sm:col-span-2 [&_form>button]:justify-self-start ' +
        '[&_label]:mb-1 [&_label]:block [&_label]:text-xs [&_label]:font-medium [&_label]:text-muted-foreground ' +
        '[&_select]:h-9 [&_select]:w-full [&_select]:rounded-md [&_select]:border [&_select]:border-input [&_select]:bg-background [&_select]:px-3 [&_select]:text-sm ' +
        '[&_table]:w-full [&_table]:border-collapse [&_table]:text-left [&_table]:text-sm ' +
        '[&_th]:border-b [&_th]:py-2 [&_th]:pr-3 [&_th]:text-xs [&_th]:font-medium [&_th]:text-muted-foreground ' +
        '[&_td]:border-b [&_td]:py-2.5 [&_td]:pr-3 [&_td]:align-top ' +
        '[&_a]:text-sm [&_a]:underline [&_a]:underline-offset-4 ' +
        className
      }
    >
      {title && <h2 className="text-base font-semibold tracking-tight">{title}</h2>}
      {description && <div className="text-sm text-muted-foreground">{description}</div>}
      {children}
    </section>
  ),
  Alert: ({ tone, children, className = '' }) => {
    const toneClass =
      tone === 'error'
        ? 'border-destructive/50 bg-destructive/10 text-destructive'
        : tone === 'warning'
          ? 'border-amber-500/40 bg-amber-500/10 text-foreground'
          : tone === 'success'
            ? 'border-emerald-500/40 bg-emerald-500/10 text-foreground'
            : 'border-border bg-muted/40 text-foreground'
    return (
      <div
        role={tone === 'error' || tone === 'warning' ? 'alert' : 'status'}
        className={`rounded-md border p-3 text-sm ${toneClass} ${className}`}
      >
        {children}
      </div>
    )
  },
}
