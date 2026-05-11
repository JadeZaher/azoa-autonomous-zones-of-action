/**
 * Example: NFT Lifecycle — Mint, Transfer, Burn
 *
 * Demonstrates the full NFT lifecycle via the SDK.
 * Run: npx tsx examples/nft-lifecycle.ts
 */
import { OasisClient } from "../src/client/index.js";
import { isOk } from "../src/core/result.js";

const API_URL = process.env.OASIS_API_URL || "http://localhost:5000";
const API_KEY = process.env.OASIS_API_KEY!;

async function main() {
  const oasis = new OasisClient({ apiUrl: API_URL, apiKey: API_KEY });

  const WALLET_ID = "00000000-0000-0000-0000-000000000001"; // replace with real

  // Mint an NFT
  const minted = await oasis.api.mintNft({
    walletId: WALLET_ID,
    name: "Cosmic Dragon #42",
    description: "A rare cosmic dragon",
    chainId: "algorand",
    imageUri: "ipfs://QmExample",
    metadata: { rarity: "legendary" },
  });
  if (!isOk(minted)) return console.error("Mint failed:", minted.error.message);
  console.log("Minted NFT");

  // Get NFT details
  // const nft = await oasis.api.getNft(nftId);

  // Get metadata (public, no auth needed)
  // const metadata = await oasis.api.getNftMetadata(nftId);

  // Transfer to another avatar
  // const transferred = await oasis.api.transferNft(nftId, {
  //   targetAvatarId: "recipient-avatar-id",
  //   walletId: WALLET_ID,
  // });

  // Burn
  // const burned = await oasis.api.burnNft(nftId, { walletId: WALLET_ID });

  // --- Avatar NFT (richer model with bindings) ---

  // Mint an Avatar NFT with holon + wallet bindings
  const avatarNft = await oasis.api.mintAvatarNFT({
    chainType: "Solana",
    name: "Soul Badge",
    description: "Soulbound identity badge",
    isSoulbound: true,
    isTransferable: false,
    holonBindings: [
      { holonId: "holon-uuid-here", role: "owner", permissionLevel: "full" },
    ],
  });
  if (isOk(avatarNft)) {
    console.log("Minted Avatar NFT:", avatarNft.value.id);

    // Get composite view (NFT + all bindings)
    const composite = await oasis.api.getAvatarNFTComposite(avatarNft.value.id);
    if (isOk(composite)) {
      console.log("Holon bindings:", composite.value.holonBindings?.length);
      console.log("Wallet bindings:", composite.value.walletBindings?.length);
    }

    // Verify ownership on-chain
    const verified = await oasis.api.verifyNFTOwnership({
      chainType: "Solana",
      nftContractAddress: "contract-address",
      tokenId: "token-id",
    });
    if (isOk(verified)) {
      console.log("Ownership verified:", verified.value.isOwner);
    }
  }
}

main().catch(console.error);
