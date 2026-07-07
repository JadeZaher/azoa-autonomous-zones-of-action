import type { Result } from "../core/result.js";
import type { SdkError } from "../core/errors.js";
import { AzoaApiClient } from "../api/client.js";
import { API_PATHS } from "../api/api-version.js";

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
  /** Clone provenance: the holon this one was cloned from. Undefined for non-clones. */
  sourceHolonId?: string;
  /** Clone provenance: the owning avatar of the source holon at clone time. Undefined for non-clones. */
  originAvatarId?: string;
}

/** Request body for {@link HolonQueryBuilder.clone}. Mirrors .NET `HolonCloneRequest`. */
export interface HolonCloneParams {
  /** Name for the cloned holon. Defaults server-side to the original name + " (Copy)". */
  name?: string;
  /** If true, clone the entire subtree (all descendants become children of the clone). */
  includeSubtree?: boolean;
}

/**
 * Fluent query builder for holons.
 *
 * Usage:
 * ```ts
 * const nfts = await azoa.holons
 *   .where({ assetType: "NFT", chainId: "algorand" })
 *   .ownedBy(avatarId)
 *   .active()
 *   .execute();
 * ```
 */
export class HolonQueryBuilder {
  private readonly api: AzoaApiClient;
  private params: HolonQueryParams = {};

  constructor(api: AzoaApiClient) {
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
    const snapshot = { ...this.params };
    this.params = {};

    const qs = new URLSearchParams();
    for (const [key, value] of Object.entries(snapshot)) {
      if (value !== undefined) qs.set(key, String(value));
    }

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
    return this.api.request<unknown>("GET", API_PATHS.HOLON_COMPOSE(id));
  }

  /**
   * Clone a holon (optionally with its subtree). The clone is a new holon
   * owned by the caller, unbound from the source's on-chain asset (tokenId is
   * reset), carrying provenance back to the source via
   * {@link HolonResult.sourceHolonId} / {@link HolonResult.originAvatarId}.
   */
  async clone(id: string, params?: HolonCloneParams): Promise<Result<HolonResult, SdkError>> {
    return this.api.request<HolonResult>("POST", API_PATHS.HOLON_CLONE(id), params ?? {});
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
