import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const config = readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), '../../next.config.js'), 'utf8')

describe('removed workflow routes', () => {
  it('sends the two retired urls to the live workflows index', () => {
    expect(config).toContain("{ source: '/workflows/action-graphs', destination: '/workflows', permanent: true }")
    expect(config).toContain("{ source: '/workflows/grants-and-bounties', destination: '/workflows', permanent: true }")
  })
})
