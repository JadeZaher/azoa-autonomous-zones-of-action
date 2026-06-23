/**
 * Example: Holon Fluent Query Builder
 *
 * Demonstrates the fluent query API for holons.
 * Run: npx tsx examples/holon-queries.ts
 */
import { AzoaClient } from "../src/client/index.js";
import { isOk } from "../src/core/result.js";

const API_URL = process.env.AZOA_API_URL || "http://localhost:5000";
const API_KEY = process.env.AZOA_API_KEY!;

async function main() {
  const azoa = new AzoaClient({ apiUrl: API_URL, apiKey: API_KEY });

  // Create a holon
  const created = await azoa.holons.create({
    name: "My NFT Collection",
    description: "A demo holon",
    assetType: "NFT",
    chainId: "algorand",
    metadata: { category: "art" },
  });
  if (!isOk(created)) return console.error(created.error.message);
  console.log("Created holon:", created.value.id);

  // Fluent queries — each execute() resets the builder
  const nfts = await azoa.holons
    .where({ assetType: "NFT", chainId: "algorand" })
    .active()
    .execute();
  if (isOk(nfts)) {
    console.log(`NFTs on Algorand: ${nfts.value.length}`);
  }

  // Get children
  const children = await azoa.holons.getChildren(created.value.id);
  if (isOk(children)) {
    console.log(`Children: ${children.value.length}`);
  }

  // Get composite view
  const composite = await azoa.holons.getComposite(created.value.id);
  if (isOk(composite)) {
    console.log("Composite:", JSON.stringify(composite.value, null, 2));
  }

  // Update
  const updated = await azoa.holons.update(created.value.id, {
    description: "Updated description",
    metadata: { category: "art", rarity: "rare" },
  });
  if (isOk(updated)) {
    console.log("Updated:", updated.value.description);
  }

  // Delete
  const deleted = await azoa.holons.delete(created.value.id);
  if (isOk(deleted)) {
    console.log("Deleted holon");
  }
}

main().catch(console.error);
