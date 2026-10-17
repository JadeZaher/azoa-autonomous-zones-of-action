import { describe, expect, it } from 'vitest'
import { CATEGORY_COLOR, CATEGORY_ORDER, NODE_CATALOG, NODE_CATALOG_BY_TYPE, type NodeCategory } from '../node-catalog'
import { NODE_OUTPUT_SHAPE } from '../node-output-schema'
import { ACTION_PRESETS, presetIsLoadable } from '../presets'

/**
 * Drift guard for the two client-side mirrors of the backend `ActionNodeType` enum.
 *
 * The mirror lagged the enum by SEVEN members once (Phase 5 D19-D21 rails landed
 * server-side with zero frontend occurrences). This list is the canonical enum
 * transcribed verbatim from `Models/Actions/ActionEnums.cs` (`public enum
 * ActionNodeType`); when a backend member is added or renamed, update the list
 * here AND the two mirrors in the same change. See `../AGENTS.md` §catalog-status.
 */
const BACKEND_ACTION_NODE_TYPES = [
  // Holon operations
  'HolonCreate', 'HolonUpdate', 'HolonDelete', 'HolonGet', 'HolonQuery', 'HolonInteract',
  'HolonGetChildren', 'HolonGetPeers', 'HolonGetAncestors', 'HolonGetDescendants',
  'HolonPropagate', 'HolonCompose', 'HolonClone', 'HolonMoveSubtree',
  // NFT operations
  'NftMint', 'NftTransfer', 'NftBurn', 'NftGet', 'NftQuery', 'NftGetMetadata',
  // Wallet operations
  'WalletCreate', 'WalletUpdate', 'WalletDelete', 'WalletGet', 'WalletQuery', 'WalletSetDefault', 'WalletGetPortfolio',
  // Action graph bundle operations
  'ActionGraphBundleGenerate', 'ActionGraphBundleDeploy',
  // Search
  'Search',
  // User NFT
  'UserNFTGetComposite',
  // Blockchain
  'BlockchainExecute',
  // Internal/control-flow
  'Condition', 'ComposeOutputs',
  // Holon-transformation nodes (Tier-1 then Tier-2)
  'GateCheck', 'Emit', 'Swap', 'Grant', 'Transfer', 'Refund', 'FungibleTokenCreate',
  // Fractionalization rails
  'Bridge', 'Back',
  // Verifiable-credential rails (Phase 5 D19)
  'CredentialRevoke',
  // Governance rails (Phase 5 D20)
  'GovernanceProposalCreate', 'GovernanceVote', 'GovernanceTally',
  // Credit rails (Phase 5 D21)
  'LoanOpen', 'LoanRepaymentRecord', 'LoanBalance',
  // Hold rails (D14 / W3.4)
  'Hold', 'Release', 'HoldRefund', 'ReleaseIntoHold',
] as const

const EXPECTED_MEMBER_COUNT = 54

/** Nodes awaiting their independent activation review. */
const PREVIEW_TYPES = ['CredentialRevoke']

const missingFrom = (present: Iterable<string>) => {
  const have = new Set(present)
  return BACKEND_ACTION_NODE_TYPES.filter((t) => !have.has(t))
}

const extraIn = (present: Iterable<string>) => {
  const canonical = new Set<string>(BACKEND_ACTION_NODE_TYPES)
  return Array.from(present).filter((t) => !canonical.has(t))
}

describe('ActionNodeType mirror drift guard', () => {
  it('the canonical list transcribes every enum member exactly once', () => {
    expect(new Set(BACKEND_ACTION_NODE_TYPES).size).toBe(BACKEND_ACTION_NODE_TYPES.length)
    expect(BACKEND_ACTION_NODE_TYPES).toHaveLength(EXPECTED_MEMBER_COUNT)
  })

  it('NODE_CATALOG carries every backend node type and nothing else', () => {
    const types = NODE_CATALOG.map((n) => n.type)

    expect(missingFrom(types), 'backend types missing from NODE_CATALOG').toEqual([])
    expect(extraIn(types), 'NODE_CATALOG types unknown to the backend enum').toEqual([])
    expect(new Set(types).size, 'duplicate catalog entries').toBe(types.length)
  })

  it('NODE_OUTPUT_SHAPE carries every backend node type and nothing else', () => {
    const types = Object.keys(NODE_OUTPUT_SHAPE)

    expect(missingFrom(types), 'backend types missing from NODE_OUTPUT_SHAPE').toEqual([])
    expect(extraIn(types), 'NODE_OUTPUT_SHAPE types unknown to the backend enum').toEqual([])
  })
})

describe('catalog invariants', () => {
  it('every category a catalog entry uses is ordered and colored', () => {
    // Array.from, not for..of over a Set: this project's TS target predates
    // downlevelIteration, so iterating a Set directly is a compile error.
    const used: NodeCategory[] = Array.from(
      new Set<NodeCategory>(NODE_CATALOG.map((n) => n.category))
    )

    for (const category of used) {
      expect(CATEGORY_ORDER, `CATEGORY_ORDER lacks ${category}`).toContain(category)
      expect(CATEGORY_COLOR[category], `CATEGORY_COLOR lacks ${category}`).toBeDefined()
    }
  })

  it('exactly the four governance/credential nodes are preview', () => {
    const preview = NODE_CATALOG.filter((n) => n.status === 'preview').map((n) => n.type)

    expect(preview.sort()).toEqual([...PREVIEW_TYPES].sort())
  })

  it('among the Phase 5 rails only CredentialRevoke requires a chain capability', () => {
    const phase5 = ['CredentialRevoke', 'GovernanceProposalCreate', 'GovernanceVote', 'GovernanceTally', 'LoanOpen', 'LoanRepaymentRecord', 'LoanBalance']
    const chainBound = phase5.filter((t) => NODE_CATALOG_BY_TYPE[t].requiresChain)

    expect(chainBound).toEqual(['CredentialRevoke'])
  })

  it('seeds money as base-unit strings, never numbers', () => {
    const loanOpen = JSON.parse(NODE_CATALOG_BY_TYPE.LoanOpen.defaultConfig)
    const repayment = JSON.parse(NODE_CATALOG_BY_TYPE.LoanRepaymentRecord.defaultConfig)

    expect(typeof loanOpen.principal).toBe('string')
    expect(typeof repayment.amount).toBe('string')
  })

  it('every defaultConfig is valid JSON', () => {
    for (const entry of NODE_CATALOG) {
      expect(() => JSON.parse(entry.defaultConfig), `${entry.type} defaultConfig`).not.toThrow()
    }
  })
})

describe('preset invariants', () => {
  it('every preset is loadable and never references a preview node type', () => {
    for (const preset of ACTION_PRESETS) {
      expect(presetIsLoadable(preset), `${preset.id} references an unknown node type`).toBe(true)
      const previewRefs = preset.nodes.filter((n) => NODE_CATALOG_BY_TYPE[n.nodeType]?.status === 'preview')
      expect(previewRefs, `${preset.id} references preview node types`).toEqual([])
    }
  })

  it('the loan tracker binds each loanHolonId from a direct predecessor loanId', () => {
    const preset = ACTION_PRESETS.find((p) => p.id === 'loan-tracker')!
    const labelByKey = new Map(preset.nodes.map((n) => [n.key, n.label]))

    for (const edge of preset.edges) {
      const target = preset.nodes.find((n) => n.key === edge.to)!
      const config = JSON.parse(target.config!)

      expect(config.loanHolonId).toEqual({ $from: `upstream.${labelByKey.get(edge.from)}.loanId` })
    }
    expect(NODE_OUTPUT_SHAPE.LoanOpen.fields.loanId).toBe('scalar')
  })
})
