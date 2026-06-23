/**
 * Example: API Key Authentication
 *
 * Demonstrates using the AZOA SDK with an API key instead of JWT.
 * Run: npx tsx examples/auth-api-key.ts
 */
import { AzoaClient } from "../src/client/index.js";
import { isOk } from "../src/core/result.js";

const API_URL = process.env.AZOA_API_URL || "http://localhost:5000";
const API_KEY = process.env.AZOA_API_KEY || "azoa_your_api_key_here";

async function main() {
  // Initialize with API key — no login needed
  const azoa = new AzoaClient({
    apiUrl: API_URL,
    apiKey: API_KEY,
  });

  // All requests automatically include X-Api-Key header
  const avatars = await azoa.api.getAllAvatars();
  if (isOk(avatars)) {
    console.log(`Found ${avatars.value.length} avatars`);
    for (const a of avatars.value) {
      console.log(`  - ${a.username} (${a.id})`);
    }
  } else {
    console.error("Failed:", avatars.error.message);
  }

  // You can also mix: use API key for server-to-server,
  // then switch to JWT for user-specific operations
  const loginResult = await azoa.auth.login("user@example.com", "password");
  if (isOk(loginResult)) {
    console.log("Logged in as:", loginResult.value.avatarId);
    // Subsequent requests use JWT (takes precedence over API key)
    const profile = await azoa.auth.getProfile();
    if (isOk(profile)) {
      console.log("Profile:", profile.value.username);
    }
  }
}

main().catch(console.error);
