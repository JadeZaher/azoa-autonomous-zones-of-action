/** Palette model: catalog entries merged with API node templates. See `AGENTS.md` §palette. */

import {
  NODE_CATALOG,
  NODE_CATALOG_BY_TYPE,
  categoryFor,
  type NodeTypeMeta,
} from './node-catalog'

/** A node template fetched from the API, normalized into palette-meta shape. */
export interface NodeTemplateMeta {
  id: string
  name: string
  nodeType: string
  description?: string
  defaultConfig?: string
}

/** One palette row: a catalog entry, optionally backed by an API node template. */
export interface PaletteEntry extends NodeTypeMeta {
  templateId?: string
  templateName?: string
}

/** Build the palette: catalog entries, augmented/overridden by API node templates. */
export function buildPalette(templates: NodeTemplateMeta[]): PaletteEntry[] {
  const entries: PaletteEntry[] = NODE_CATALOG.map((c) => ({ ...c }))
  for (const t of templates) {
    const base = NODE_CATALOG_BY_TYPE[t.nodeType]
    entries.push({
      type: t.nodeType,
      category: base?.category ?? categoryFor(t.nodeType),
      label: t.name,
      description: t.description ?? base?.description ?? `Template for ${t.nodeType}`,
      defaultConfig: t.defaultConfig ?? base?.defaultConfig ?? '{}',
      requiresChain: base?.requiresChain,
      templateId: t.id,
      templateName: t.name,
    })
  }
  return entries
}
