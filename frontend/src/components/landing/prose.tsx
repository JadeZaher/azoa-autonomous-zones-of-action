import Link from 'next/link'
import { Fragment, type ReactNode } from 'react'
import { sourceLinksOwnUrls } from '@/content/workflows'
import type { Block, Run, WorkflowSource } from '@/content/workflows'

function InlineText({ text }: { text: string }) {
  const matches = [...text.matchAll(/`([^`\n]+)`/g)]
  if (matches.length === 0) return text
  const nodes: ReactNode[] = []
  let cursor = 0
  for (const match of matches) {
    const start = match.index ?? 0
    if (start > cursor) nodes.push(text.slice(cursor, start))
    nodes.push(<code key={start}>{match[1]}</code>)
    cursor = start + match[0].length
  }
  if (cursor < text.length) nodes.push(text.slice(cursor))
  return nodes
}

function RunView({ run }: { run: Run }) {
  if (typeof run === 'string') return <InlineText text={run} />
  if ('href' in run) {
    const label = run.strong ? <strong>{run.text}</strong> : run.text
    return run.href.startsWith('/') ? <Link href={run.href}>{label}</Link> : <a href={run.href}>{label}</a>
  }
  if ('em' in run) return <em>{run.em}</em>
  return <strong>{run.strong}</strong>
}

function Runs({ runs }: { runs: readonly Run[] }) {
  return runs.map((run, index) => (
    <Fragment key={index}>
      <RunView run={run} />
    </Fragment>
  ))
}

function SourceLinks({ source }: { source: WorkflowSource }) {
  if (sourceLinksOwnUrls(source)) return null
  if (source.linkLabel) return <> <a href={source.urls[0]}>{source.linkLabel}</a></>
  return source.urls.map((url, index) => (
    <Fragment key={url}>{index === 0 ? ' ' : ' and '}<a href={url}>{url}</a></Fragment>
  ))
}

function SourceList({ sources }: { sources: readonly WorkflowSource[] }) {
  return (
    <>
      <h2 className="az-arch-h2">Sources</h2>
      <ol>
        {sources.map((source) => (
          <li key={source.name}>
            {source.runs ? <Runs runs={source.runs} /> : source.name}
            <SourceLinks source={source} />
          </li>
        ))}
      </ol>
    </>
  )
}

export function Prose({
  blocks,
  sources = [],
}: {
  blocks: readonly Block[]
  sources?: readonly WorkflowSource[]
}) {
  return blocks.map((block, index) => {
    if (block.type === 'h2') return <h2 key={index} className="az-arch-h2">{block.text}</h2>
    if (block.type === 'p') return <p key={index}><Runs runs={block.runs} /></p>
    if (block.type === 'sources') return <SourceList key={index} sources={sources} />
    return (
      <ul key={index}>
        {block.items.map((item, itemIndex) => (
          <li key={itemIndex}><Runs runs={item} /></li>
        ))}
      </ul>
    )
  })
}
