/**
 * Example: Wallet CRUD + Portfolio
 *
 * Demonstrates typed wallet methods and portfolio aggregation.
 * Run: npx tsx examples/wallet-management.ts
 */
import { OasisClient } from "../src/client/index.js";
import { isOk } from "../src/core/result.js";

const API_URL = process.env.OASIS_API_URL || "http://localhost:5000";
const API_KEY = process.env.OASIS_API_KEY!;

async function main() {
  const oasis = new OasisClient({ apiUrl: API_URL, apiKey: API_KEY });

  // Create a wallet
  const created = await oasis.api.createWallet({
    chainType: "Solana",
    address: "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM",
    label: "My Solana Wallet",
    isDefault: true,
  });
  if (!isOk(created)) return console.error(created.error.message);
  console.log("Created wallet:", created.value.id);

  // List wallets with filters
  const wallets = await oasis.api.listWallets({ chainType: "Solana" });
  if (isOk(wallets)) {
    console.log(`Solana wallets: ${wallets.value.length}`);
    for (const w of wallets.value) {
      console.log(`  - ${w.label ?? w.address} (default: ${w.isDefault})`);
    }
  }

  // Get portfolio for a specific wallet
  const portfolio = await oasis.api.getWalletPortfolio(created.value.id);
  if (isOk(portfolio)) {
    console.log(`Balance: ${portfolio.value.balance} ${portfolio.value.symbol}`);
    console.log(`NFTs held: ${portfolio.value.nfts.length}`);
  }

  // Set as default
  await oasis.api.setDefaultWallet(created.value.id);
  console.log("Set as default wallet");

  // Update label
  const updated = await oasis.api.updateWallet(created.value.id, { label: "Primary Solana" });
  if (isOk(updated)) {
    console.log("Updated label:", updated.value.label);
  }

  // Cross-chain portfolio aggregation (uses chain providers)
  // const fullPortfolio = await oasis.portfolio.getAll(avatarId);

  // Delete
  const deleted = await oasis.api.deleteWallet(created.value.id);
  if (isOk(deleted)) {
    console.log("Wallet deleted");
  }
}

main().catch(console.error);
