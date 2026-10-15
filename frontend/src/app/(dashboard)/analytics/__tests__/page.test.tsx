import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type {
  PositionSummary,
  PositionsResponse,
  TransactionHistoryEntry,
  TransactionHistoryPage,
} from 'azoa-sdk'

/**
 * D16 invariant tests. The page is rendered against a mocked SDK whose
 * responses carry exactly the two facts the backend can decline to state — a
 * row it never priced and a row whose quantity it does not know — and the
 * assertions check that neither is ever rendered as a zero.
 */
const { apiMock } = vi.hoisted(() => ({
  apiMock: {
    getAnalyticsTransactions: vi.fn(),
    getAnalyticsPositions: vi.fn(),
  },
}))

vi.mock('@/lib/azoa', () => ({
  azoa: { api: apiMock },
  isOk: (r: { ok: boolean }) => r.ok === true,
}))

vi.mock('@/lib/azoa-context', () => ({
  useAzoa: () => ({ userId: 'user-1' }),
}))

import AnalyticsPage from '../page'

function makeEntry(overrides: Partial<TransactionHistoryEntry> & { operationId: string }): TransactionHistoryEntry {
  return {
    timestamp: '2026-09-01T12:00:00.000Z',
    operationType: 'Transfer',
    status: 'Confirmed',
    chainType: 'Algorand',
    amountStatus: 'Known',
    isStampable: true,
    pricing: 'NeverPriced',
    ...overrides,
  }
}

function makePosition(overrides: Partial<PositionSummary> = {}): PositionSummary {
  return {
    chainType: 'Algorand',
    quantity: '1500',
    entryCount: 2,
    unsettledEntryCount: 0,
    unclassifiedEntryCount: 0,
    unknownAmountEntryCount: 0,
    isQuantityComplete: true,
    pricedEntryCount: 0,
    unpricedEntryCount: 2,
    unstampableEntryCount: 0,
    isValueComplete: false,
    ...overrides,
  }
}

/** One unpriced-but-known row, one unknown-quantity row — both unpriced, as every row is today. */
const unpricedEntry = makeEntry({ operationId: 'op-unpriced', amount: '250000', pricing: 'NeverPriced' })
const unknownEntry = makeEntry({ operationId: 'op-unknown', amountStatus: 'Unknown', amount: undefined })

const transactionsPage: TransactionHistoryPage = {
  items: [unpricedEntry, unknownEntry],
  skip: 0,
  take: 25,
  hasMore: true,
  unpricedEntryCount: 2,
  unknownAmountEntryCount: 1,
}

const knownPosition = makePosition({ assetId: '777' })
const incompletePosition = makePosition({
  assetId: undefined,
  quantity: '0',
  unknownAmountEntryCount: 1,
  isQuantityComplete: false,
})

const positionsResponse: PositionsResponse = {
  items: [knownPosition, incompletePosition],
  scannedEntryCount: 4,
  isComplete: true,
}

beforeEach(() => {
  apiMock.getAnalyticsTransactions.mockResolvedValue({ ok: true, value: transactionsPage })
  apiMock.getAnalyticsPositions.mockResolvedValue({ ok: true, value: positionsResponse })
})

describe('AnalyticsPage — D16 absent facts render as absent', () => {
  it('renders an unpriced entry as "Unpriced" and an unknown quantity as "Unknown", never as 0', async () => {
    render(<AnalyticsPage />)

    await screen.findByTestId('tx-row-op-unpriced')

    // The unpriced row keeps its known amount verbatim and says Unpriced for value.
    expect(screen.getByTestId('tx-amount-op-unpriced')).toHaveTextContent('250000')
    expect(screen.getByTestId('tx-value-op-unpriced')).toHaveTextContent('Unpriced')

    // The unknown-quantity row says Unknown for amount, and is still unpriced.
    expect(screen.getByTestId('tx-amount-op-unknown')).toHaveTextContent('Unknown')
    expect(screen.getByTestId('tx-amount-op-unknown')).not.toHaveTextContent('0')
    expect(screen.getByTestId('tx-value-op-unknown')).toHaveTextContent('Unpriced')

    // The missing-picture counts the backend returns are surfaced, not hidden.
    expect(screen.getByTestId('tx-unpriced-count')).toHaveTextContent('2 of 2 unpriced')
    expect(screen.getByTestId('tx-unknown-count')).toHaveTextContent('1 of 2 unknown amount')

    // No cell anywhere collapsed an absent fact into a bare zero.
    expect(screen.queryAllByText(/^0(\.0+)?$/)).toHaveLength(0)
  })

  it('renders an incomplete position as "Incomplete" rather than its partial quantity, and its value as Unpriced', async () => {
    render(<AnalyticsPage />)

    await screen.findByTestId('pos-row-Algorand:native')

    const incompleteQuantity = screen.getByTestId('pos-quantity-Algorand:native')
    expect(incompleteQuantity).toHaveTextContent('Incomplete')
    expect(incompleteQuantity).toHaveTextContent('1 unknown amount')
    expect(incompleteQuantity).not.toHaveTextContent(/\b0\b/)

    // The complete position shows its quantity verbatim — no arithmetic, no reformatting.
    expect(screen.getByTestId('pos-quantity-Algorand:777')).toHaveTextContent('1500')

    // Value is absent for both today: every row is unpriced by design.
    expect(screen.getByTestId('pos-value-Algorand:777')).toHaveTextContent('Unpriced')
    expect(screen.getByTestId('pos-value-Algorand:777')).toHaveTextContent('2 of 2 unpriced')
    expect(screen.getByTestId('pos-value-Algorand:native')).toHaveTextContent('Unpriced')

    expect(screen.queryAllByText(/^0(\.0+)?$/)).toHaveLength(0)
  })

  it('pages the transaction history through skip/take and applies the date window as ISO bounds', async () => {
    render(<AnalyticsPage />)
    await screen.findByTestId('tx-row-op-unpriced')

    expect(apiMock.getAnalyticsTransactions).toHaveBeenLastCalledWith({ skip: 0, take: 25 })

    fireEvent.click(screen.getByRole('button', { name: /Next/ }))
    await waitFor(() =>
      expect(apiMock.getAnalyticsTransactions).toHaveBeenLastCalledWith({ skip: 25, take: 25 }),
    )

    fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-09-01' } })
    fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-09-02' } })
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }))

    // Applying a window resets to the first page and forwards the same bounds to positions.
    await waitFor(() =>
      expect(apiMock.getAnalyticsTransactions).toHaveBeenLastCalledWith({
        from: '2026-09-01T00:00:00.000Z',
        to: '2026-09-02T23:59:59.999Z',
        skip: 0,
        take: 25,
      }),
    )
    expect(apiMock.getAnalyticsPositions).toHaveBeenLastCalledWith({
      from: '2026-09-01T00:00:00.000Z',
      to: '2026-09-02T23:59:59.999Z',
    })
  })
})
