/**
 * The mechanical half of the product thesis: "a sponsor never meets the chain." A commit message
 * saying the vocabulary is gone is not a control -- a grep that fails the build is. This file is
 * that grep, run as part of the normal `npm test` suite (vitest picks up any `src/**\/*.test.ts`)
 * so a future label can't reintroduce the words below without either rewording it or adding a
 * reasoned line to `allowlist.json`.
 *
 * What counts as "rendered copy", scanned under `src/app` and `src/components`:
 *   - JSX text (everything between tags a person reads)
 *   - a string literal assigned to a `label`, `title`, `placeholder`, or `aria-label` key --
 *     whether that's a literal JSX prop (`<Input label="...">`) or a plain object property in a
 *     data-driven config (`{ label: "...", icon: Wallet }`), since both render the same way to a
 *     screen. Config-driven UI (nav lists, palettes, table columns) is exactly where a defect the
 *     ledger already found -- the sidebar's "DeFi" nav group -- was hiding.
 *   - every string leaf value in a JSON file under a `messages/`, `locales/`, or `i18n/`
 *     directory, in case this app ever grows an i18n message catalogue (it has none today, so
 *     this path is presently a no-op; it exists so the net does not have to be rebuilt later).
 * Deliberately NOT scanned: identifiers, imports, type-level string literals, object/enum keys,
 * comparisons, className-shaped helpers (cn/clsx/cva), and any other string position -- a wire
 * value nobody reads is not copy. `src/lib` is not scanned; it is where this control itself lives,
 * and it mixes copy modules with logic that must say what it means.
 */

import { describe, expect, it } from 'vitest'
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import allowlistJson from './allowlist.json'
import { CHAIN_WORDS, type ChainWord } from './chain-words'

const HERE = dirname(fileURLToPath(import.meta.url))
/** frontend/ -- three levels above src/lib/vocabulary/. */
const CLIENT_ROOT = resolve(HERE, '../../..')

/** Directories whose rendered copy a person can reach. Kept in sync with SCANNED_DIRECTORIES below. */
const SCANNED_DIRECTORIES = ['src/app', 'src/components']

/** JSX/object-literal keys that carry rendered copy, per this file's own doc comment above. */
const COPY_KEY_NAMES = new Set(['label', 'title', 'placeholder', 'aria-label'])

/** Directory name segments (case-insensitive) that mark a JSON file as an i18n message catalogue. */
const MESSAGE_DIRECTORY_NAMES = new Set(['messages', 'locales', 'i18n'])

// ---------------------------------------------------------------------------
// Allowlist shape and matching
// ---------------------------------------------------------------------------

interface PathAllowlistEntry {
  pattern: string
  reason: string
}

interface StringAllowlistEntry {
  word: string
  file?: string
  pathPrefix?: string
  text?: string
  reason: string
}

interface Allowlist {
  paths: PathAllowlistEntry[]
  strings: StringAllowlistEntry[]
}

const allowlist = allowlistJson as unknown as Allowlist

/** Converts a `paths[].pattern` glob (`**` = any depth, `*` = one path segment) into a RegExp. */
function globToRegExp(glob: string): RegExp {
  let source = ''
  for (let i = 0; i < glob.length; i += 1) {
    const char = glob[i]
    if (char === '*' && glob[i + 1] === '*') {
      source += '.*'
      i += 1
    } else if (char === '*') {
      source += '[^/]*'
    } else if ('.+^${}()|[]\\'.includes(char)) {
      source += '\\' + char
    } else {
      source += char
    }
  }
  return new RegExp('^' + source + '$')
}

function isReasoned(reason: unknown): reason is string {
  return typeof reason === 'string' && reason.trim().length > 0
}

function isPathAllowlisted(relativeFile: string): boolean {
  return allowlist.paths.some(
    (entry) => isReasoned(entry.reason) && globToRegExp(entry.pattern).test(relativeFile),
  )
}

interface Violation {
  file: string
  line: number
  word: string
  text: string
}

function isStringAllowlisted(violation: Violation): boolean {
  return allowlist.strings.some((entry) => {
    if (!isReasoned(entry.reason) || entry.word !== violation.word) return false
    if (entry.file && entry.file !== violation.file) return false
    if (entry.pathPrefix && !violation.file.startsWith(entry.pathPrefix)) return false
    if (entry.text && !violation.text.includes(entry.text)) return false
    return Boolean(entry.file || entry.pathPrefix)
  })
}

function isAllowlisted(violation: Violation): boolean {
  return isPathAllowlisted(violation.file) || isStringAllowlisted(violation)
}

// ---------------------------------------------------------------------------
// Recorded debt (the ratchet)
// ---------------------------------------------------------------------------

/**
 * `allowlist.json` says "this word is fine here". `known-debt.json` says the opposite: these
 * are real thesis violations on AZOA's console that this wave did NOT fix, recorded so the
 * gate can still enforce that no NEW one is added.
 *
 * They are separate on purpose. Folding this list into the allowlist would have required
 * writing a `reason` claiming each hit is justified, and none of them are -- `(dashboard)`
 * gates on `isAuthenticated` alone (src/app/(dashboard)/layout.tsx), with no role check, so
 * every signed-in user reaches /blockchain, /fungible-mint, /bridge and the seed-phrase
 * export. Clearing the debt is a role-separation decision, not a reword.
 */
/**
 * `files[relativePath][word]` is HOW MANY hits of that word that file still has - precisely,
 * how many distinct rendered SPANS in that file still carry it, because the scanner emits one
 * violation per (span, word). Adding a new label, paragraph or config entry with the word moves
 * the number; a second occurrence inside one sentence that is already flagged does not.
 *
 * It used to be a bare `string[]` of words, so the ratchet was keyed on (file, word) alone.
 * "The list can only shrink" was then true of the PAIR list and false of the violation count:
 * `wallet-export-dialog.tsx` is recorded for mnemonic/seed-phrase/private-key, so any amount
 * of additional seed-phrase copy could be added to that file and the gate stayed green. The
 * count is the ratchet; the pair list is only its index.
 */
interface KnownDebt {
  files: Record<string, Record<string, number>>
}

const DEBT_FILE = join(HERE, 'known-debt.json')

/**
 * Separates the file from the word in a debt key. A NUL, written as an ESCAPE: the
 * character cannot occur in a path or a word id, so the key is unambiguous even for a path
 * with a space in it - but a raw 0x00 byte in the source made git and grep call this file
 * binary, and a reviewer read the invisible byte as a space and reported the comment as
 * contradicting the code. Same character, no binary byte.
 */
const DEBT_KEY_SEPARATOR = '\0'

function debtKeyOf(violation: Violation): string {
  return `${violation.file}${DEBT_KEY_SEPARATOR}${violation.word}`
}

/** How many live hits each (file, word) pair currently has. */
function countByKey(violations: Violation[]): Map<string, number> {
  const counts = new Map<string, number>()
  for (const violation of violations) {
    const key = debtKeyOf(violation)
    counts.set(key, (counts.get(key) ?? 0) + 1)
  }
  return counts
}

/**
 * Loads the recorded debt, FAILING when the file is absent rather than writing one. A guard
 * that authors the baseline it checks passes green over whatever it happens to find on the
 * first run -- the ledger already records that exact defect (RUNBOOK.md §2). Regenerating is
 * therefore an explicit, opt-in act.
 */
function loadKnownDebt(): KnownDebt {
  if (!existsSync(DEBT_FILE)) {
    throw new Error(
      `known-debt.json is missing (${DEBT_FILE}). This gate does not auto-author its own ` +
        `baseline. Restore the file from git, or regenerate it deliberately with ` +
        `AZOA_REGENERATE_VOCABULARY_DEBT=1 npx vitest run src/lib/vocabulary`,
    )
  }
  return JSON.parse(readFileSync(DEBT_FILE, 'utf8')) as KnownDebt
}

/** The recorded ceiling for each (file, word) pair. Absent means the pair is not recorded. */
function recordedDebtCounts(debt: KnownDebt): Map<string, number> {
  const counts = new Map<string, number>()
  for (const [file, words] of Object.entries(debt.files ?? {})) {
    for (const [word, count] of Object.entries(words)) {
      counts.set(`${file}${DEBT_KEY_SEPARATOR}${word}`, count)
    }
  }
  return counts
}

function regenerateKnownDebt(violations: Violation[]): void {
  const byFile = new Map<string, Map<string, number>>()
  for (const violation of violations) {
    const words = byFile.get(violation.file) ?? new Map<string, number>()
    words.set(violation.word, (words.get(violation.word) ?? 0) + 1)
    byFile.set(violation.file, words)
  }
  const files: Record<string, Record<string, number>> = {}
  for (const file of Array.from(byFile.keys()).sort()) {
    const words = byFile.get(file)!
    const counted: Record<string, number> = {}
    for (const word of Array.from(words.keys()).sort()) counted[word] = words.get(word)!
    files[file] = counted
  }
  const document = {
    _readme:
      'Chain vocabulary that reaches rendered copy on AZOA\'s console and is NOT yet fixed. ' +
      'This is recorded DEBT, not permission: unlike allowlist.json, no entry here claims the ' +
      'word is justified. Each entry is a COUNT: how many hits of that word that file still ' +
      'has. The gate fails on any hit outside this list, on any count above the recorded one, ' +
      'and again once a listed hit is cleaned and the number is not lowered - so the numbers ' +
      'can only go down. Regenerate with ' +
      'AZOA_REGENERATE_VOCABULARY_DEBT=1 npx vitest run src/lib/vocabulary',
    files,
  }
  writeFileSync(DEBT_FILE, JSON.stringify(document, null, 2) + '\n', 'utf8')
}

// ---------------------------------------------------------------------------
// Extracting the spans a person can actually see
// ---------------------------------------------------------------------------

/** The `label`/`title`/`placeholder`/`aria-label` key a literal sits under, JSX prop or object property. */
function copyKeyNameOf(node: ts.Node): string | null {
  const parent = node.parent
  if (!parent) return null
  if (ts.isJsxAttribute(parent)) return parent.name.getText()
  if (ts.isJsxExpression(parent) && parent.parent && ts.isJsxAttribute(parent.parent)) {
    return parent.parent.name.getText()
  }
  if (ts.isPropertyAssignment(parent) && parent.initializer === node) {
    return parent.name.getText().replace(/^["']|["']$/g, '')
  }
  return null
}

interface Span {
  line: number
  text: string
}

function collectVisibleSpans(sourceFile: ts.SourceFile): Span[] {
  const spans: Span[] = []
  const record = (node: ts.Node, rawText: string) => {
    const trimmed = rawText.trim()
    if (trimmed.length === 0 || !/[A-Za-z]/.test(trimmed)) return
    const { line } = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile))
    spans.push({ line: line + 1, text: trimmed })
  }
  const visit = (node: ts.Node) => {
    if (ts.isJsxText(node)) {
      record(node, node.text)
    } else if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
      const key = copyKeyNameOf(node)
      if (key && COPY_KEY_NAMES.has(key)) record(node, node.text)
    } else if (ts.isTemplateExpression(node)) {
      const key = copyKeyNameOf(node)
      if (key && COPY_KEY_NAMES.has(key)) {
        record(node, node.head.text)
        for (const span of node.templateSpans) record(node, span.literal.text)
      }
    }
    ts.forEachChild(node, visit)
  }
  visit(sourceFile)
  return spans
}

/** Every string leaf value in a JSON value, for an i18n message catalogue. */
function collectJsonStrings(value: unknown, out: string[] = []): string[] {
  if (typeof value === 'string') {
    out.push(value)
  } else if (Array.isArray(value)) {
    for (const item of value) collectJsonStrings(item, out)
  } else if (value && typeof value === 'object') {
    for (const item of Object.values(value)) collectJsonStrings(item, out)
  }
  return out
}

function isMessageCatalogueFile(relativeFile: string): boolean {
  if (!relativeFile.endsWith('.json')) return false
  const segments = relativeFile.split('/')
  return segments.some((segment) => MESSAGE_DIRECTORY_NAMES.has(segment.toLowerCase()))
}

// ---------------------------------------------------------------------------
// Walking the tree
// ---------------------------------------------------------------------------

function isExcludedFile(relativeFile: string): boolean {
  return (
    relativeFile.endsWith('.test.ts') ||
    relativeFile.endsWith('.test.tsx') ||
    relativeFile.endsWith('.spec.ts') ||
    relativeFile.endsWith('.spec.tsx') ||
    relativeFile.endsWith('.d.ts')
  )
}

function walkDirectory(directory: string, files: string[] = []): string[] {
  for (const entry of readdirSync(directory)) {
    const candidate = join(directory, entry)
    if (statSync(candidate).isDirectory()) {
      walkDirectory(candidate, files)
    } else if (
      candidate.endsWith('.ts') ||
      candidate.endsWith('.tsx') ||
      candidate.endsWith('.json')
    ) {
      files.push(candidate)
    }
  }
  return files
}

function wordsMatching(text: string): ChainWord[] {
  return CHAIN_WORDS.filter((word) => word.pattern.test(text))
}

function scanForChainWords(clientRoot: string, scannedDirectories: string[]) {
  const violations: Violation[] = []
  let scannedFiles = 0

  for (const scanned of scannedDirectories) {
    const directory = join(clientRoot, scanned)
    if (!existsSync(directory)) continue

    for (const file of walkDirectory(directory)) {
      const relativeFile = relative(clientRoot, file).split(sep).join('/')
      if (isExcludedFile(relativeFile)) continue
      scannedFiles += 1

      if (relativeFile.endsWith('.json')) {
        if (!isMessageCatalogueFile(relativeFile)) continue
        const parsed = JSON.parse(readFileSync(file, 'utf8')) as unknown
        for (const text of collectJsonStrings(parsed)) {
          for (const word of wordsMatching(text)) {
            violations.push({ file: relativeFile, line: 1, word: word.id, text: text.slice(0, 160) })
          }
        }
        continue
      }

      const source = readFileSync(file, 'utf8')
      const sourceFile = ts.createSourceFile(
        relativeFile,
        source,
        ts.ScriptTarget.Latest,
        true,
        relativeFile.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
      )
      for (const span of collectVisibleSpans(sourceFile)) {
        for (const word of wordsMatching(span.text)) {
          violations.push({
            file: relativeFile,
            line: span.line,
            word: word.id,
            text: span.text.replace(/\s+/g, ' ').slice(0, 160),
          })
        }
      }
    }
  }

  return { scannedFiles, violations }
}

function formatViolations(violations: Violation[]): string {
  const lines = violations.map((v) => `  ${v.file}:${v.line}  [${v.word}]  ${v.text}`)
  return (
    `Chain vocabulary reached rendered copy in ${violations.length} place(s), outside the ` +
    `committed allowlist (src/lib/vocabulary/allowlist.json):\n\n${lines.join('\n')}\n\n` +
    `A non-crypto sponsor must not see a chain name, a wallet, a mint, a hash, or the word ` +
    `"token" where "funds"/"stake"/"units" would do. Reword the copy, or add a reasoned entry ` +
    `to allowlist.json if this specific string is genuinely a different, non-blockchain sense ` +
    `of the word.`
  )
}

// ---------------------------------------------------------------------------
// Extraction and matching self-tests
// ---------------------------------------------------------------------------

function spansOf(source: string, fileName = 'fixture.tsx'): Span[] {
  const sourceFile = ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  return collectVisibleSpans(sourceFile)
}

describe('collectVisibleSpans', () => {
  it('reads JSX text as copy', () => {
    const spans = spansOf('const El = () => <p>Your wallet address is ready</p>')
    expect(spans.map((s) => s.text)).toContain('Your wallet address is ready')
  })

  it('reads a string literal on a label/title/placeholder/aria-label JSX prop', () => {
    const spans = spansOf('const El = () => <Input aria-label="Wallet address" placeholder="0x..." />')
    expect(spans.map((s) => s.text)).toEqual(expect.arrayContaining(['Wallet address', '0x...']))
  })

  it('reads a string literal on a label/title key in a plain data object', () => {
    const spans = spansOf('const navGroups = [{ label: "DeFi", items: [] }]', 'fixture.ts')
    expect(spans.map((s) => s.text)).toContain('DeFi')
  })

  it('does not read a className, an import path, or an identifier', () => {
    const spans = spansOf(
      [
        'import { cn } from "~/lib/utils"',
        'const wallet = "chain"',
        'const El = () => <div className={cn("wallet-chain")}>{wallet}</div>',
      ].join('\n'),
    )
    expect(spans).toHaveLength(0)
  })

  it('does not read a comment', () => {
    const spans = spansOf('// mint an NFT on-chain\nconst El = () => <p>ok</p>')
    expect(spans.map((s) => s.text)).toEqual(['ok'])
  })
})

describe('allowlist matching', () => {
  it('every allowlist entry carries a non-empty reason', () => {
    const unreasoned = [
      ...allowlist.paths.filter((entry) => !isReasoned(entry.reason)).map((e) => `path:${e.pattern}`),
      ...allowlist.strings
        .filter((entry) => !isReasoned(entry.reason))
        .map((e) => `string:${e.word}@${e.file ?? e.pathPrefix ?? '?'}`),
    ]
    expect(unreasoned, `entries with no reason: ${unreasoned.join(', ')}`).toHaveLength(0)
  })

  it('every string entry names a file or a pathPrefix, and every path pattern is a non-empty glob', () => {
    for (const entry of allowlist.strings) {
      expect(Boolean(entry.file || entry.pathPrefix), JSON.stringify(entry)).toBe(true)
    }
    for (const entry of allowlist.paths) {
      expect(entry.pattern.length > 0, JSON.stringify(entry)).toBe(true)
    }
  })

  it('exempts a hit under an allowlisted path', () => {
    expect(
      isAllowlisted({ file: 'src/app/operator/login/page.tsx', line: 1, word: 'node', text: 'Node operator' }),
    ).toBe(true)
  })

  it('does not exempt a word the allowlist never named for that file', () => {
    expect(
      isAllowlisted({ file: 'src/app/(dashboard)/actions/page.tsx', line: 1, word: 'blockchain', text: 'blockchain operations' }),
    ).toBe(false)
  })

  it('rejects an entry with no reason, even if it would otherwise match', () => {
    const bogus: Allowlist = {
      paths: [],
      strings: [{ word: 'mint', file: 'src/app/(dashboard)/nfts/page.tsx', reason: '' }],
    }
    const matches = bogus.strings.some(
      (entry) => isReasoned(entry.reason) && entry.word === 'mint' && entry.file === 'src/app/(dashboard)/nfts/page.tsx',
    )
    expect(matches).toBe(false)
  })
})

// ---------------------------------------------------------------------------
// The ratchet itself
// ---------------------------------------------------------------------------

describe('the recorded-debt ratchet', () => {
  const hit = (file: string, word: string, line: number): Violation => ({
    file,
    word,
    line,
    text: 'irrelevant',
  })

  const FILE = 'src/a.tsx'
  const WORD = 'seed-phrase'
  // Built through debtKeyOf, never hard-coded: the separator is a NUL (DEBT_KEY_SEPARATOR),
  // and a literal key would be asserting a property of this test's own fixture rather than of
  // the ratchet.
  const KEY = debtKeyOf(hit(FILE, WORD, 1))
  const debt: KnownDebt = { files: { [FILE]: { [WORD]: 1 } } }

  it('counts a pair rather than only recording it', () => {
    const counts = countByKey([hit(FILE, WORD, 3), hit(FILE, WORD, 9)])
    expect(counts.get(KEY)).toBe(2)
  })

  it('sees a SECOND hit added inside a pair the debt already records', () => {
    // The case the (file, word) key could not see: the pair IS recorded, so the "outside the
    // recorded debt" assertion passes, and only the count catches the new occurrence.
    const live = countByKey([hit(FILE, WORD, 3), hit(FILE, WORD, 9)])
    const recorded = recordedDebtCounts(debt)

    expect(recorded.has(KEY)).toBe(true)
    expect(live.get(KEY)!).toBeGreaterThan(recorded.get(KEY)!)
  })

  it('sees a hit that was cleaned but left recorded', () => {
    const live = countByKey([])
    const recorded = recordedDebtCounts(debt)

    expect(live.get(KEY) ?? 0).toBeLessThan(recorded.get(KEY)!)
  })
})
// ---------------------------------------------------------------------------
// The gate
// ---------------------------------------------------------------------------

describe('repo-wide chain vocabulary', () => {
  const { scannedFiles, violations } = scanForChainWords(CLIENT_ROOT, SCANNED_DIRECTORIES)
  const unallowed = violations.filter((violation) => !isAllowlisted(violation))

  if (process.env.AZOA_REGENERATE_VOCABULARY_DEBT === '1') regenerateKnownDebt(unallowed)

  const recorded = recordedDebtCounts(loadKnownDebt())
  const live = countByKey(unallowed)

  it('actually reads a non-empty tree', () => {
    // Without this, every assertion below is vacuously true on a mis-resolved CLIENT_ROOT.
    expect(scannedFiles).toBeGreaterThan(0)
  })

  it('adds no chain word outside the allowlist and the recorded debt', () => {
    const added = unallowed.filter((violation) => !recorded.has(debtKeyOf(violation)))
    expect(added, added.length > 0 ? formatViolations(added) : '').toHaveLength(0)
  })

  it('adds no further hit inside a pair the debt already records', () => {
    // The hole the pair-keyed ratchet had: a recorded (file, word) pair absorbed unlimited
    // NEW occurrences of that word in that file and stayed green.
    const grown = Array.from(live.entries())
      .filter(([key, count]) => recorded.has(key) && count > recorded.get(key)!)
      .map(([key, count]) => `  ${key}: ${recorded.get(key)} recorded, ${count} found`)
      .sort()
    expect(
      grown,
      grown.length > 0
        ? `known-debt.json records fewer hits than the tree now has:\n\n` +
            grown.join('\n') +
            `\n\nThe numbers may only go down. Reword the new copy rather than raising one.`
        : '',
    ).toHaveLength(0)
  })

  it('records no debt that has since been cleaned', () => {
    const stale = Array.from(recorded.entries())
      .filter(([key, count]) => (live.get(key) ?? 0) < count)
      .map(([key, count]) => `  ${key}: ${count} recorded, ${live.get(key) ?? 0} found`)
      .sort()
    expect(
      stale,
      stale.length > 0
        ? `known-debt.json still lists ${stale.length} hit(s) that no longer exist:\n\n` +
            stale.join('\n') +
            `\n\nLower or delete them so the ratchet cannot slip back. It may only shrink.`
        : '',
    ).toHaveLength(0)
  })
})
