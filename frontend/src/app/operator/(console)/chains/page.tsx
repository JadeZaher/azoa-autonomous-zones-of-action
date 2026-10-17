'use client'

import { OperatorPageHeader } from '@/components/operator/operator-ui'
import { StuckSignatures } from '@/components/operator/stuck-signatures'
import { DexSettleReview, EthereumRecovery, WttOperator } from '@/components/operator/chain-recovery'

export default function OperatorChainsPage() {
  return (
    <div className="flex flex-col gap-8">
      <OperatorPageHeader
        eyebrow="Chains"
        title="Chain operations that need a person"
        description="Every action here is re-proven from chain state by the node. Nothing on this page can declare an outcome the chain does not show."
      />
      <StuckSignatures />
      <DexSettleReview />
      <WttOperator />
      <EthereumRecovery />
    </div>
  )
}
