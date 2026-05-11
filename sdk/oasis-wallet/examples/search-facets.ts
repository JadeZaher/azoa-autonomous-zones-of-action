/**
 * Example: Search + Faceted Filtering
 *
 * Demonstrates the search API with typed params.
 * Run: npx tsx examples/search-facets.ts
 */
import { OasisClient } from "../src/client/index.js";
import { isOk } from "../src/core/result.js";

const API_URL = process.env.OASIS_API_URL || "http://localhost:5000";
const API_KEY = process.env.OASIS_API_KEY!;

async function main() {
  const oasis = new OasisClient({ apiUrl: API_URL, apiKey: API_KEY });

  // Get available facets
  const facets = await oasis.api.getSearchFacets();
  if (isOk(facets)) {
    console.log("Available facets:", JSON.stringify(facets.value, null, 2));
  }

  // Search for NFTs on Algorand
  const results = await oasis.api.search({
    query: "dragon",
    assetType: "NFT",
    chainId: "algorand",
    sortBy: "createdDate",
    sortDescending: true,
    page: 1,
    pageSize: 10,
  });
  if (isOk(results)) {
    console.log(`Found ${results.value.totalCount} results (page ${results.value.page}/${Math.ceil(results.value.totalCount / results.value.pageSize)})`);
    for (const item of results.value.items) {
      console.log("  -", JSON.stringify(item));
    }
  }

  // Search with date range
  const recent = await oasis.api.search({
    query: "*",
    createdAfter: "2025-01-01",
    createdBefore: "2026-12-31",
    pageSize: 5,
  });
  if (isOk(recent)) {
    console.log(`Recent items: ${recent.value.totalCount}`);
  }
}

main().catch(console.error);
