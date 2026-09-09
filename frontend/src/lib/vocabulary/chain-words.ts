/**
 * The chain-word list `vocabulary.test.ts` greps rendered copy for. See that file for what
 * counts as "rendered" and `allowlist.json` for the committed exceptions.
 *
 * Each pattern is matched case-insensitively, with word boundaries, against one span of visible
 * text. `id` is the stable key an allowlist entry references — renaming an `id` orphans every
 * allowlist entry that names it, so treat it as part of this file's public shape.
 */

export interface ChainWord {
  id: string
  pattern: RegExp
}

export const CHAIN_WORDS: readonly ChainWord[] = [
  { id: 'blockchain', pattern: /\bblockchain\b/i },
  { id: 'algorand', pattern: /\balgorand\b/i },
  { id: 'asa', pattern: /\basa\b/i },
  { id: 'wallet-address', pattern: /\bwallet[\s-]+address\b/i },
  { id: 'mnemonic', pattern: /\bmnemonic\b/i },
  { id: 'seed-phrase', pattern: /\bseed[\s-]+phrase\b/i },
  { id: 'gas', pattern: /\bgas\b/i },
  { id: 'gas-fee', pattern: /\bgas[\s-]+fee\b/i },
  { id: 'tx-hash', pattern: /\btx[\s_-]?hash\b/i },
  { id: 'transaction-hash', pattern: /\btransaction[\s-]+hash\b/i },
  { id: 'on-chain', pattern: /\bon[\s-]?chain\b/i },
  { id: 'chain', pattern: /\bchain\b/i },
  { id: 'mint', pattern: /\bmints?\b|\bminting\b/i },
  { id: 'minted', pattern: /\bminted\b/i },
  { id: 'node', pattern: /\bnodes?\b/i },
  { id: 'rail', pattern: /\brails?\b/i },
  { id: 'defi', pattern: /\bdefi\b/i },
  { id: 'token', pattern: /\btokens?\b/i },
  { id: 'tokenomics', pattern: /\btokenomics\b/i },
  { id: 'signed-challenge', pattern: /\bsigned[\s-]+challenge\b/i },
  { id: 'private-key', pattern: /\bprivate[\s-]+key\b/i },
  { id: 'custody', pattern: /\bcustody\b/i },
] as const
