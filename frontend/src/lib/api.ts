import { readInitialApiUrl } from './runtime-config'

// Resolved at runtime (server-injected window global), not baked at `next
// build` — see lib/runtime-config.ts.
const API_BASE_URL = readInitialApiUrl()

export interface BlockchainResponse<T = any> {
  success: boolean
  result?: T
  message?: string
  error?: string
}

export interface BalanceRequest {
  address: string
  tokenId?: string
}

export interface BalanceResponse {
  address: string
  balance: string
  tokenId?: string
  timestamp: Date
}

export interface AddressValidationRequest {
  address: string
}

export interface AddressValidationResponse {
  isValid: boolean
  message: string
  exists: boolean
}

export interface TransferRequest {
  tokenId?: string
  fromAddress: string
  toAddress: string
  amount: number
}

export interface TransferResponse {
  transactionId: string
  message: string
  timestamp: Date
}

export interface TransactionStatusRequest {
  txHash: string
}

export interface TransactionStatusResponse {
  txHash: string
  status: 'pending' | 'confirmed' | 'failed'
  block?: string
  fee?: string
  timestamp: Date
}

export interface TokenMetadataRequest {
  tokenId: string
}

export interface TokenMetadataResponse {
  tokenId: string
  name?: string
  symbol?: string
  totalSupply?: string
  decimals?: number
  timestamp: Date
}

export interface TokensByOwnerRequest {
  address: string
}

export interface TokenInfo {
  address: string
  balance: string
  tokenId?: string
  name?: string
  symbol?: string
  decimals?: number
  timestamp: Date
}

export class BlockchainApiClient {
  private baseUrl: string

  constructor(baseUrl: string = API_BASE_URL) {
    this.baseUrl = baseUrl
  }

  async getBalance(request: BalanceRequest): Promise<BlockchainResponse<BalanceResponse>> {
    try {
      const response = await fetch(`${this.baseUrl}/api/blockchain/balance`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(request),
      })

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      return await response.json()
    } catch (error) {
      console.error('Error fetching balance:', error)
      return {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      }
    }
  }

  async validateAddress(request: AddressValidationRequest): Promise<BlockchainResponse<AddressValidationResponse>> {
    try {
      const response = await fetch(`${this.baseUrl}/api/blockchain/validate-address`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(request),
      })

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      return await response.json()
    } catch (error) {
      console.error('Error validating address:', error)
      return {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      }
    }
  }

  async transfer(request: TransferRequest): Promise<BlockchainResponse<TransferResponse>> {
    try {
      const response = await fetch(`${this.baseUrl}/api/blockchain/transfer`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(request),
      })

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      return await response.json()
    } catch (error) {
      console.error('Error transferring:', error)
      return {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      }
    }
  }

  async getTransactionStatus(request: TransactionStatusRequest): Promise<BlockchainResponse<TransactionStatusResponse>> {
    try {
      const response = await fetch(`${this.baseUrl}/api/blockchain/transaction-status`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(request),
      })

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      return await response.json()
    } catch (error) {
      console.error('Error fetching transaction status:', error)
      return {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      }
    }
  }

  async getTokenMetadata(request: TokenMetadataRequest): Promise<BlockchainResponse<TokenMetadataResponse>> {
    try {
      const response = await fetch(`${this.baseUrl}/api/blockchain/token-metadata`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(request),
      })

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      return await response.json()
    } catch (error) {
      console.error('Error fetching token metadata:', error)
      return {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      }
    }
  }

  async getTokensByOwner(request: TokensByOwnerRequest): Promise<BlockchainResponse<TokenInfo[]>> {
    try {
      const response = await fetch(`${this.baseUrl}/api/blockchain/tokens-by-owner`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(request),
      })

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      return await response.json()
    } catch (error) {
      console.error('Error fetching tokens by owner:', error)
      return {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      }
    }
  }

  async getChainInfo(chainType: string = 'algorand'): Promise<BlockchainResponse<any>> {
    try {
      const response = await fetch(`${this.baseUrl}/api/blockchain/chain-info/${chainType}`)
      
      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      return await response.json()
    } catch (error) {
      console.error('Error fetching chain info:', error)
      return {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      }
    }
  }
}

// Singleton instance
export const apiClient = new BlockchainApiClient()