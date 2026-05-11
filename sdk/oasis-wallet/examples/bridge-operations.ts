/**
 * Example: Cross-Chain Bridge Operations
 *
 * Demonstrates the bridge workflow: routes → initiate → fetch VAA → redeem → complete.
 * Bridge endpoints return bare objects (not OASISResult-wrapped).
 * Run: npx tsx examples/bridge-operations.ts
 */
import { OasisClient } from "../src/client/index.js";
import { isOk } from "../src/core/result.js";

const API_URL = process.env.OASIS_API_URL || "http://localhost:5000";
const API_KEY = process.env.OASIS_API_KEY!;

async function main() {
  const oasis = new OasisClient({ apiUrl: API_URL, apiKey: API_KEY });

  // 1. Check available bridge routes
  const routes = await oasis.api.getBridgeRoutes();
  if (!isOk(routes)) return console.error(routes.error.message);
  console.log("Available bridge routes:");
  for (const r of routes.value) {
    console.log(`  ${r.sourceChain} → ${r.targetChain} (modes: ${r.availableModes.join(", ")})`);
  }

  // 2. Initiate a bridge transfer
  const bridge = await oasis.api.initiateBridge({
    sourceChain: "algorand",
    targetChain: "solana",
    tokenId: "ALGO",
    recipientAddress: "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM",
    amount: 10,
    mode: "Wormhole",
  });
  if (!isOk(bridge)) return console.error(bridge.error.message);
  console.log("Bridge initiated:", bridge.value.id, "status:", bridge.value.status);

  const bridgeId = bridge.value.id;

  // 3. Poll for VAA (Wormhole mode)
  const vaa = await oasis.api.fetchVAA(bridgeId);
  if (isOk(vaa)) {
    console.log("VAA ready, signatures:", vaa.value.vaaSignatureCount);
  }

  // 4. Redeem on target chain
  const redeemed = await oasis.api.redeemBridge(bridgeId);
  if (isOk(redeemed)) {
    console.log("Redeem tx:", redeemed.value.redemptionTxHash);
  }

  // 5. Complete
  const completed = await oasis.api.completeBridge(bridgeId);
  if (isOk(completed)) {
    console.log("Bridge completed:", completed.value.status);
  }

  // 6. History
  const history = await oasis.api.getBridgeHistory();
  if (isOk(history)) {
    console.log(`Bridge history: ${history.value.length} transactions`);
  }
}

main().catch(console.error);
