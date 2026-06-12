'use client'

import { useState, useEffect, useCallback } from 'react'
import { oasis, isOk, safeUnwrap } from './oasis'
import type { BalanceInfo, HolonResult, PortfolioSummary, ChainBalance } from './oasis'
import { useNetwork } from './network-context'

// ─── useBalance ───

export function useBalance(chain: string, address: string | null, tokenId?: string) {
  const { networkKey } = useNetwork()
  const [balance, setBalance] = useState<BalanceInfo | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    if (!address) return
    setLoading(true)
    setError(null)
    const result = await oasis.wallet.getBalance(chain, address, tokenId)
    if (isOk(result)) {
      setBalance(result.value)
    } else {
      setError(result.error.message)
    }
    setLoading(false)
    // `networkKey` is a dep so balances refetch on switch AND once the
    // backend RPC config lands (endpoints may change under the same network).
  }, [chain, address, tokenId, networkKey])

  useEffect(() => { refresh() }, [refresh])

  return { balance, loading, error, refresh }
}

// ─── usePortfolio ───

export function usePortfolio(avatarId: string | null) {
  const { networkKey } = useNetwork()
  const [portfolio, setPortfolio] = useState<PortfolioSummary | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    if (!avatarId) return
    setLoading(true)
    setError(null)
    const result = await oasis.portfolio.getAll(avatarId)
    if (isOk(result)) {
      setPortfolio(result.value)
    } else {
      setError(result.error.message)
    }
    setLoading(false)
  }, [avatarId, networkKey])

  useEffect(() => { refresh() }, [refresh])

  return { portfolio, loading, error, refresh }
}

// ─── useHolons ───

export function useHolons(filters?: {
  name?: string
  avatarId?: string
  chainId?: string
  assetType?: string
  isActive?: boolean
}) {
  const [holons, setHolons] = useState<HolonResult[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)

    const builder = oasis.holons
    if (filters) builder.where(filters)

    const result = await builder.execute()
    if (isOk(result)) {
      setHolons(result.value)
    } else {
      setError(result.error.message)
    }
    setLoading(false)
  }, [filters?.name, filters?.avatarId, filters?.chainId, filters?.assetType, filters?.isActive])

  useEffect(() => { refresh() }, [refresh])

  return { holons, loading, error, refresh }
}

// ─── useChainInfo ───

export function useChainInfo(chain: string) {
  const { networkKey } = useNetwork()
  const [info, setInfo] = useState<Record<string, unknown> | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const fetch = async () => {
      setLoading(true)
      const result = await oasis.wallet.getChainInfo(chain)
      if (isOk(result)) {
        setInfo(result.value)
      } else {
        setError(result.error.message)
      }
      setLoading(false)
    }
    fetch()
  }, [chain, networkKey])

  return { info, loading, error }
}

// ─── useWallets ───

export function useWallets(avatarId: string | null) {
  const [wallets, setWallets] = useState<Array<{ id: string; chainType: string; address: string; label?: string; isDefault: boolean }>>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    if (!avatarId) return
    setLoading(true)
    setError(null)
    const result = await oasis.api.listWallets({ avatarId })
    if (isOk(result)) {
      setWallets(result.value)
    } else {
      setError(result.error.message)
    }
    setLoading(false)
  }, [avatarId])

  useEffect(() => { refresh() }, [refresh])

  return { wallets, loading, error, refresh }
}
