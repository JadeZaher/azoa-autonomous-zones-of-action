import { OperatorShell } from '@/components/operator/operator-shell'

export default function OperatorConsoleLayout({ children }: { children: React.ReactNode }) {
  return <OperatorShell>{children}</OperatorShell>
}
