/**
 * Example: Holon Fluent Query Builder
 *
 * Demonstrates the fluent query API for holons.
 * Run: npx tsx examples/holon-queries.ts
 */
import { OasisClient } from "../src/client/index.js";
import { isOk } from "../src/core/result.js";

const API_URL = process.env.OASIS_API_URL || "http://localhost:5000";
const API_KEY = process.env.OASIS_API_KEY!;

async function main() {
  const oasis = new OasisClient({ apiUrl: API_URL, apiKey: API_KEY });

  // Create a holon
  const created = await oasis.holons.create({
    name: "My NFT Collection",
    description: "A demo holon",
    assetType: "NFT",
    chainId: "algorand",
    metadata: { category: "art" },
  });
  if (!isOk(created)) return console.error(created.error.message);
  console.log("Created holon:", created.value.id);

  // Fluent queries — each execute() resets the builder
  const nfts = await oasis.holons
    .where({ assetType: "NFT", chainId: "algorand" })
    .active()
    .execute();
  if (isOk(nfts)) {
    console.log(`NFTs on Algorand: ${nfts.value.length}`);
  }

  // Get children
  const children = await oasis.holons.getChildren(created.value.id);
  if (isOk(children)) {
    console.log(`Children: ${children.value.length}`);
  }

  // Get composite view
  const composite = await oasis.holons.getComposite(created.value.id);
  if (isOk(composite)) {
    console.log("Composite:", JSON.stringify(composite.value, null, 2));
  }

  // Update
  const updated = await oasis.holons.update(created.value.id, {
    description: "Updated description",
    metadata: { category: "art", rarity: "rare" },
  });
  if (isOk(updated)) {
    console.log("Updated:", updated.value.description);
  }

  // Delete
  const deleted = await oasis.holons.delete(created.value.id);
  if (isOk(deleted)) {
    console.log("Deleted holon");
  }
}

main().catch(console.error);
