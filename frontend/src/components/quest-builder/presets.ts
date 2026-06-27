/**
 * Ready-made DAG / quest flow presets for the visual builder.
 *
 * Each preset is a declarative graph: a list of nodes (referencing built-in
 * `QuestNodeType` names from the catalog) and edges (by node `key`). Loading a
 * preset materializes valid React Flow nodes/edges with entry/terminal flags
 * already set and a clean auto-layout, so a new user can start from a working
 * shape instead of a blank canvas.
 *
 * Presets intentionally use only Tier-0/Tier-1 node types (no on-chain
 * `requiresChain` nodes) so they pass DAG validation and can be saved without a
 * tenant chain capability. The "Token Launch" preset is the one exception and
 * is flagged accordingly.
 */

import { NODE_CATALOG_BY_TYPE } from './node-catalog'

/** A node inside a preset, addressed by a stable `key` for edge wiring. */
export interface PresetNode {
  /** Stable identifier within the preset; edges reference these. */
  key: string
  /** Backend QuestNodeType name (must exist in NODE_CATALOG). */
  nodeType: string
  /** Optional label override; defaults to the catalog label. */
  label?: string
  /** Optional config override (JSON string); defaults to the catalog default. */
  config?: string
  isEntry?: boolean
  isTerminal?: boolean
}

export interface PresetEdge {
  from: string
  to: string
  edgeType?: string
  condition?: string
}

export interface QuestPreset {
  id: string
  name: string
  /** One-line summary shown in the preset menu. */
  description: string
  /** True if any node requires an on-chain capability to execute. */
  requiresChain?: boolean
  nodes: PresetNode[]
  edges: PresetEdge[]
}

export const QUEST_PRESETS: QuestPreset[] = [
  {
    id: 'linear-holon',
    name: 'Linear Holon Pipeline',
    description: 'Create → Interact → Get. The simplest valid 3-node flow.',
    nodes: [
      { key: 'create', nodeType: 'HolonCreate', isEntry: true },
      { key: 'interact', nodeType: 'HolonInteract' },
      { key: 'get', nodeType: 'HolonGet', isTerminal: true },
    ],
    edges: [
      { from: 'create', to: 'interact' },
      { from: 'interact', to: 'get' },
    ],
  },
  {
    id: 'gated-branch',
    name: 'Gated Branch',
    description: 'Entry → Gate Check → two outcome branches that each terminate.',
    nodes: [
      { key: 'start', nodeType: 'HolonCreate', label: 'Start', isEntry: true },
      { key: 'gate', nodeType: 'GateCheck', config: '{\n  "expression": "output.ok == true"\n}' },
      { key: 'pass', nodeType: 'Emit', label: 'On Pass', config: '{\n  "event": "approved",\n  "payload": {}\n}', isTerminal: true },
      { key: 'fail', nodeType: 'Emit', label: 'On Fail', config: '{\n  "event": "rejected",\n  "payload": {}\n}', isTerminal: true },
    ],
    edges: [
      { from: 'start', to: 'gate' },
      { from: 'gate', to: 'pass', condition: 'true' },
      { from: 'gate', to: 'fail', condition: 'false' },
    ],
  },
  {
    id: 'fan-out-compose',
    name: 'Fan-out & Compose',
    description: 'One entry fans out to parallel fetches, then Compose Outputs merges them.',
    nodes: [
      { key: 'root', nodeType: 'HolonGet', label: 'Load Root', isEntry: true },
      { key: 'children', nodeType: 'HolonGetChildren', label: 'Get Children' },
      { key: 'peers', nodeType: 'HolonGetPeers', label: 'Get Peers' },
      { key: 'ancestors', nodeType: 'HolonGetAncestors', label: 'Get Ancestors' },
      { key: 'compose', nodeType: 'ComposeOutputs', config: '{\n  "sources": ["children", "peers", "ancestors"]\n}', isTerminal: true },
    ],
    edges: [
      { from: 'root', to: 'children' },
      { from: 'root', to: 'peers' },
      { from: 'root', to: 'ancestors' },
      { from: 'children', to: 'compose' },
      { from: 'peers', to: 'compose' },
      { from: 'ancestors', to: 'compose' },
    ],
  },
  {
    id: 'nft-mint-flow',
    name: 'NFT Mint & Verify',
    description: 'Create wallet → Mint NFT → fetch metadata → emit a settlement event.',
    nodes: [
      { key: 'wallet', nodeType: 'WalletCreate', label: 'Ensure Wallet', isEntry: true },
      { key: 'mint', nodeType: 'NftMint' },
      { key: 'meta', nodeType: 'NftGetMetadata', label: 'Verify Metadata' },
      { key: 'emit', nodeType: 'Emit', label: 'Emit Minted', config: '{\n  "event": "nft.minted",\n  "payload": {}\n}', isTerminal: true },
    ],
    edges: [
      { from: 'wallet', to: 'mint' },
      { from: 'mint', to: 'meta' },
      { from: 'meta', to: 'emit' },
    ],
  },
  {
    id: 'star-onboarding',
    name: 'STAR Onboarding',
    description: 'Generate a STAR dapp, deploy it, then create a root holon for it.',
    nodes: [
      { key: 'gen', nodeType: 'StarGenerate', label: 'Generate STAR', isEntry: true },
      { key: 'deploy', nodeType: 'StarDeploy', label: 'Deploy STAR' },
      { key: 'root', nodeType: 'HolonCreate', label: 'Create Root Holon', isTerminal: true },
    ],
    edges: [
      { from: 'gen', to: 'deploy' },
      { from: 'deploy', to: 'root' },
    ],
  },
  {
    id: 'token-launch',
    name: 'Token Launch (on-chain)',
    description: 'Asset holon → fungible token → grant. Requires a chain capability.',
    requiresChain: true,
    nodes: [
      { key: 'asset', nodeType: 'HolonCreate', label: 'Asset Holon', config: '{\n  "name": "BackingAsset",\n  "holonType": "Asset"\n}', isEntry: true },
      { key: 'token', nodeType: 'FungibleTokenCreate', label: 'Launch Token' },
      { key: 'grant', nodeType: 'Grant', label: 'Grant Initial Supply', isTerminal: true },
    ],
    edges: [
      { from: 'asset', to: 'token' },
      { from: 'token', to: 'grant' },
    ],
  },

  // ─── Scrum Lifecycle (ArdaNova / scrum-lifecycle-quest-presets track) ───
  // Each preset maps to §5.1 of the ARDANOVA-AZOA-INTEGRATION-CONTRACT.
  // GateCheck configs use the authoritative GateCheckNodeConfig shape (§5.2):
  //   { "predicate": "<bool expr>", "reads": { "<name>": <json> } }

  {
    id: 'scrum-create-project',
    name: 'Create Project',
    description: 'Create a Project holon then emit project.created. Entry point for the scrum lifecycle.',
    nodes: [
      {
        key: 'create',
        nodeType: 'HolonCreate',
        label: 'Create Project Holon',
        config: '{\n  "name": "NewProject",\n  "holonType": "Project"\n}',
        isEntry: true,
      },
      {
        key: 'emit',
        nodeType: 'Emit',
        label: 'Emit project.created',
        config: '{\n  "event": "project.created",\n  "payload": {}\n}',
        isTerminal: true,
      },
    ],
    edges: [
      { from: 'create', to: 'emit' },
    ],
  },

  {
    id: 'scrum-fund-project',
    name: 'Fund Project',
    description: 'Gate on funding goal met → mint ProjectShare token → grant initial allocation. Requires a chain capability.',
    requiresChain: true,
    nodes: [
      {
        key: 'gate',
        nodeType: 'GateCheck',
        label: 'Funding Goal Met?',
        config: '{\n  "predicate": "reads.fundingGoalMet == true",\n  "reads": {\n    "fundingGoalMet": false\n  }\n}',
        isEntry: true,
      },
      {
        key: 'token',
        nodeType: 'FungibleTokenCreate',
        label: 'Mint ProjectShare Token',
        config: '{\n  "chainType": "Algorand",\n  "name": "ProjectShare",\n  "unitName": "PSHARE",\n  "total": 1000000,\n  "decimals": 6,\n  "holonId": null\n}',
      },
      {
        key: 'grant',
        nodeType: 'Grant',
        label: 'Grant Initial Allocation',
        config: '{\n  "request": {\n    "name": "ProjectShare",\n    "amount": "0"\n  },\n  "holonId": null\n}',
        isTerminal: true,
      },
    ],
    edges: [
      { from: 'gate', to: 'token', condition: 'true' },
      { from: 'token', to: 'grant' },
    ],
  },

  {
    id: 'scrum-start-work',
    name: 'Start Work',
    description: 'Gate on project status == FUNDED → emit sprint.started. Safe to run without chain.',
    nodes: [
      {
        key: 'gate',
        nodeType: 'GateCheck',
        label: 'Status == FUNDED?',
        config: '{\n  "predicate": "reads.projectStatus == \\"FUNDED\\"",\n  "reads": {\n    "projectStatus": "DRAFT"\n  }\n}',
        isEntry: true,
      },
      {
        key: 'emit',
        nodeType: 'Emit',
        label: 'Emit sprint.started',
        config: '{\n  "event": "sprint.started",\n  "payload": {}\n}',
        isTerminal: true,
      },
    ],
    edges: [
      { from: 'gate', to: 'emit', condition: 'true' },
    ],
  },

  {
    id: 'scrum-task-reward',
    name: 'Task Reward (Bounty)',
    description: 'Gate on submission accepted → transfer reward → emit task.completed; rejection branch → emit task.rejected. Requires a chain capability.',
    requiresChain: true,
    nodes: [
      {
        key: 'gate',
        nodeType: 'GateCheck',
        label: 'Submission Accepted?',
        config: '{\n  "predicate": "reads.submissionAccepted == true",\n  "reads": {\n    "submissionAccepted": false\n  }\n}',
        isEntry: true,
      },
      {
        key: 'transfer',
        nodeType: 'Transfer',
        label: 'Transfer Reward',
        config: '{\n  "nftId": "",\n  "request": {\n    "to": "",\n    "amount": "0"\n  }\n}',
      },
      {
        key: 'emit-completed',
        nodeType: 'Emit',
        label: 'Emit task.completed',
        config: '{\n  "event": "task.completed",\n  "payload": {}\n}',
        isTerminal: true,
      },
      {
        key: 'refund',
        nodeType: 'Refund',
        label: 'Refund Escrow',
        config: '{\n  "ref": ""\n}',
      },
      {
        key: 'emit-rejected',
        nodeType: 'Emit',
        label: 'Emit task.rejected',
        config: '{\n  "event": "task.rejected",\n  "payload": {}\n}',
        isTerminal: true,
      },
    ],
    edges: [
      { from: 'gate', to: 'transfer', condition: 'true' },
      { from: 'transfer', to: 'emit-completed' },
      { from: 'gate', to: 'refund', condition: 'false' },
      { from: 'refund', to: 'emit-rejected' },
    ],
  },

  {
    id: 'scrum-project-lifecycle',
    name: 'Project Lifecycle',
    description: 'Combined create → fund gate → token launch → grant → work gate → sprint started. Full scrum lifecycle DAG.',
    requiresChain: true,
    nodes: [
      {
        key: 'create',
        nodeType: 'HolonCreate',
        label: 'Create Project Holon',
        config: '{\n  "name": "NewProject",\n  "holonType": "Project"\n}',
        isEntry: true,
      },
      {
        key: 'emit-created',
        nodeType: 'Emit',
        label: 'Emit project.created',
        config: '{\n  "event": "project.created",\n  "payload": {}\n}',
      },
      {
        key: 'gate-fund',
        nodeType: 'GateCheck',
        label: 'Funding Goal Met?',
        config: '{\n  "predicate": "reads.fundingGoalMet == true",\n  "reads": {\n    "fundingGoalMet": false\n  }\n}',
      },
      {
        key: 'token',
        nodeType: 'FungibleTokenCreate',
        label: 'Mint ProjectShare Token',
        config: '{\n  "chainType": "Algorand",\n  "name": "ProjectShare",\n  "unitName": "PSHARE",\n  "total": 1000000,\n  "decimals": 6,\n  "holonId": null\n}',
      },
      {
        key: 'grant',
        nodeType: 'Grant',
        label: 'Grant Initial Allocation',
        config: '{\n  "request": {\n    "name": "ProjectShare",\n    "amount": "0"\n  },\n  "holonId": null\n}',
      },
      {
        key: 'gate-work',
        nodeType: 'GateCheck',
        label: 'Status == FUNDED?',
        config: '{\n  "predicate": "reads.projectStatus == \\"FUNDED\\"",\n  "reads": {\n    "projectStatus": "DRAFT"\n  }\n}',
      },
      {
        key: 'emit-sprint',
        nodeType: 'Emit',
        label: 'Emit sprint.started',
        config: '{\n  "event": "sprint.started",\n  "payload": {}\n}',
        isTerminal: true,
      },
    ],
    edges: [
      { from: 'create', to: 'emit-created' },
      { from: 'emit-created', to: 'gate-fund' },
      { from: 'gate-fund', to: 'token', condition: 'true' },
      { from: 'token', to: 'grant' },
      { from: 'grant', to: 'gate-work' },
      { from: 'gate-work', to: 'emit-sprint', condition: 'true' },
    ],
  },
]

/** A preset is loadable only if every node type it references exists in the catalog. */
export function presetIsLoadable(preset: QuestPreset): boolean {
  return preset.nodes.every((n) => NODE_CATALOG_BY_TYPE[n.nodeType] != null)
}
