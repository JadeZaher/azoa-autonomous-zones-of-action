import { expect, it } from "vitest";
import { nodeConfig } from "../../src/workflow/node-config.js";

it("preserves exact NFT fee and protocol receipts in the frozen grant draft", () => {
  const feeAuthorization = { revision: 3, quoteId: "platform-receipt", maxNativeBaseUnits: "1000000000" };
  const protocolCostAuthorization = { quoteId: "cost-receipt", quoteHash: "receipt-hash",
    limits: { networkFee: "18446744073709551615", retainedRent: "9646560", protocolLevy: "10000000" } };
  const body = JSON.parse(nodeConfig.grant({ request: {
    walletId: "payer", name: "NFT", chainId: "Solana", metadataUri: "ipfs://immutable",
    feeAuthorization, protocolCostAuthorization,
  } }));
  expect(body.request.metadataUri).toBe("ipfs://immutable");
  expect(body.request.feeAuthorization).toEqual(feeAuthorization);
  expect(body.request.protocolCostAuthorization).toEqual(protocolCostAuthorization);
  expect(body.request).not.toHaveProperty("tokenId");
});
