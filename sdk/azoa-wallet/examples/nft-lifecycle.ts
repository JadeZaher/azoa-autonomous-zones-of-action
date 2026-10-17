// Run with real wallet, metadata and a stable request key; reuse that key after interruptions.
import { AzoaClient } from "../src/client/index.js";
import { isOk } from "../src/core/result.js";

function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`Set ${name} before running this example.`);
  return value;
}

async function main() {
  const azoa = new AzoaClient({ apiUrl: process.env.AZOA_API_URL || "http://localhost:5000", apiKey: required("AZOA_API_KEY") });
  const operationId = process.env.AZOA_NFT_OPERATION_ID;
  const chainType = operationId ? '' : required("AZOA_NFT_CHAIN");
  const quote = operationId ? null : await azoa.api.getNftFeeQuote(chainType, "Mint");
  if (quote && !isOk(quote)) throw new Error(quote.error.message);
  const fee = quote && isOk(quote) ? quote.value : null;
  if (fee) {
    console.log("Platform fee:", fee.nativeBaseUnits, "base units of", fee.currency, "on", fee.network, "; chain fees additional");
    if (required("AZOA_NFT_APPROVED_FEE_BASE_UNITS") !== fee.nativeBaseUnits)
      throw new Error("Review the quote, then set the approved fee amount before submitting.");
  }
  const result = operationId
    ? await azoa.api.refreshNftLifecycle(operationId)
    : await azoa.api.mintUserNFT({
        chainType,
        feeAuthorization: fee ? { revision: fee.revision, maxNativeBaseUnits: fee.nativeBaseUnits, quoteId: fee.quoteId } : undefined, walletId: required("AZOA_NFT_WALLET_ID"),
        name: required("AZOA_NFT_NAME"), metadataURI: required("AZOA_NFT_METADATA_URI"),
      }, required("AZOA_NFT_REQUEST_KEY"));
  if (!isOk(result)) throw new Error(result.error.message);
  console.log("Operation:", result.value.operationId, "State:", result.value.state, "Stage:", result.value.stage);
  if (result.value.state !== "Completed") {
    console.log("Keep this operation reference. Confirmation or participant authorization is still required.");
    return;
  }
  console.log("Confirmed asset:", result.value.assetId, "Transaction:", result.value.transactionId);
  const nft = await azoa.api.getUserNFT(result.value.nftId);
  if (isOk(nft)) console.log("Recorded NFT:", nft.value.id);
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
