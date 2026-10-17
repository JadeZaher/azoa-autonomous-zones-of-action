'use client'

import { useAzoa } from '@/lib/azoa-context'
import { HoldRecoveryPanel } from '@/components/hold/hold-recovery-panel'

export default function HoldsPage() {
  const { userId } = useAzoa()
  return <div className="space-y-4"><h1 className="text-lg font-semibold">Hold recovery</h1>
    {userId ? <HoldRecoveryPanel actorId={userId} scope={userId} /> : <p>Sign in to review custody commands.</p>}
  </div>
}
