import type { Result } from "../core/result.js";
import type { SdkError } from "../core/errors.js";
import { OasisApiClient } from "../api/client.js";

/**
 * Matches .NET HolonQueryRequest fields.
 * Sent as query parameters to GET /api/holon.
 */
export interface HolonQueryParams {
  name?: string;
  avatarId?: string;
  providerName?: string;
  chainId?: string;
  assetType?: string;
  isActive?: boolean;
  parentHolonId?: string;
}

export interface HolonResult {
  id: string;
  name: string;
  description?: string;
  avatarId?: string;
  parentHolonId?: string;
  providerName?: string;
  chainId?: string;
  assetType?: string;
  tokenId?: string;
  metadata?: Record<string, string>;
  peerHolonIds?: string[];
  isActive: boolean;
  createdDate?: string;
  modifiedDate?: string;
}

/**
 * Fluent query builder for holons.
 *
 * Usage:
 * ```ts
 * const nfts = await oasis.holons
 *   .where({ assetType: "NFT", chainId: "algorand" })
 *   .ownedBy(avatarId)
 *   .active()
 *   .execute();
 * ```
 */
export class HolonQueryBuilder {
  private readonly api: OasisApiClient;
  private params: HolonQueryParams = {};

  constructor(api: OasisApiClient) {
    this.api = api;
  }

  /** Filter by multiple fields at once. */
  where(filters: Partial<HolonQueryParams>): this {
    Object.assign(this.params, filters);
    return this;
  }

  /** Filter by name. */
  named(name: string): this {
    this.params.name = name;
    return this;
  }

  /** Filter by owning avatar. */
  ownedBy(avatarId: string): this {
    this.params.avatarId = avatarId;
    return this;
  }

  /** Filter by chain. */
  onChain(chainId: string): this {
    this.params.chainId = chainId;
    return this;
  }

  /** Filter by asset type (e.g., "NFT"). */
  ofType(assetType: string): this {
    this.params.assetType = assetType;
    return this;
  }

  /** Filter by provider. */
  onProvider(providerName: string): this {
    this.params.providerName = providerName;
    return this;
  }

  /** Filter by parent holon. */
  childrenOf(parentId: string): this {
    this.params.parentHolonId = parentId;
    return this;
  }

  /** Only active holons. */
  active(): this {
    this.params.isActive = true;
    return this;
  }

  /** Only inactive holons. */
  inactive(): this {
    this.params.isActive = false;
    return this;
  }

  /** Execute the query and return results. */
  async execute(): Promise<Result<HolonResult[], SdkError>> {
    // Build query string from non-undefined params
    const qs = new URLSearchParams();
    for (const [key, value] of Object.entries(this.params)) {
      if (value !== undefined) qs.set(key, String(value));
    }

    // Use the API client's internal request mechanism
    // GET /api/holon?name=X&avatarId=Y&...
    return this.api.request<HolonResult[]>("GET", `/api/holon?${qs}`);
  }

  /** Get a specific holon by ID. */
  async get(id: string): Promise<Result<HolonResult, SdkError>> {
    return this.api.request<HolonResult>("GET", `/api/holon/${id}`);
  }

  /** Get children of a holon. */
  async getChildren(id: string): Promise<Result<HolonResult[], SdkError>> {
    return this.api.request<HolonResult[]>("GET", `/api/holon/${id}/children`);
  }

  /** Get ancestors (walk up the tree). */
  async getAncestors(id: string): Promise<Result<HolonResult[], SdkError>> {
    return this.api.request<HolonResult[]>("GET", `/api/holon/${id}/ancestors`);
  }

  /** Get descendants (walk down the tree). */
  async getDescendants(id: string): Promise<Result<HolonResult[], SdkError>> {
    return this.api.request<HolonResult[]>("GET", `/api/holon/${id}/descendants`);
  }

  /** Get peer holons. */
  async getPeers(id: string): Promise<Result<HolonResult[], SdkError>> {
    return this.api.request<HolonResult[]>("GET", `/api/holon/${id}/peers`);
  }

  /** Get composite view. */
  async getComposite(id: string): Promise<Result<unknown, SdkError>> {
    return this.api.request<unknown>("GET", `/api/holon/${id}/compose`);
  }

  /** Create a new holon. */
  async create(params: {
    name: string;
    description?: string;
    providerName?: string;
    chainId?: string;
    assetType?: string;
    metadata?: Record<string, string>;
    peerHolonIds?: string[];
  }): Promise<Result<HolonResult, SdkError>> {
    return this.api.request<HolonResult>("POST", "/api/holon", params);
  }

  /** Update a holon. */
  async update(id: string, params: Partial<{
    name: string;
    description: string;
    providerName: string;
    chainId: string;
    assetType: string;
    metadata: Record<string, string>;
    peerHolonIds: string[];
    isActive: boolean;
  }>): Promise<Result<HolonResult, SdkError>> {
    return this.api.request<HolonResult>("PUT", `/api/holon/${id}`, params);
  }

  /** Delete a holon. */
  async delete(id: string): Promise<Result<{ message: string }, SdkError>> {
    return this.api.request<{ message: string }>("DELETE", `/api/holon/${id}`);
  }
}
