/**
 * Example: User CRUD Operations
 *
 * Full user lifecycle: register, login, get, update, list, delete.
 * Run: npx tsx examples/user-crud.ts
 */
import { AzoaClient } from "../src/client/index.js";
import { isOk } from "../src/core/result.js";

const API_URL = process.env.AZOA_API_URL || "http://localhost:5000";

async function main() {
  const azoa = new AzoaClient({ apiUrl: API_URL });

  // Register
  const reg = await azoa.auth.register({
    email: "demo@example.com",
    password: "SecureP@ss1",
    username: "demo_user",
    firstName: "Demo",
  });
  if (!isOk(reg)) {
    console.error("Register failed:", reg.error.message);
    return;
  }
  console.log("Registered:", reg.value.userId);

  // Login (already done by register, but shown explicitly)
  const login = await azoa.auth.login("demo@example.com", "SecureP@ss1");
  if (isOk(login)) {
    console.log("Logged in, token obtained");
  }

  // Get profile
  const profile = await azoa.auth.getProfile();
  if (isOk(profile)) {
    console.log("Profile:", profile.value.username, profile.value.email);
  }

  // Update user
  const updated = await azoa.api.updateUser(reg.value.userId, {
    firstName: "Updated",
    lastName: "User",
  });
  if (isOk(updated)) {
    console.log("Updated name to:", updated.value.firstName, updated.value.lastName);
  }

  // List all users
  const all = await azoa.api.getAllUsers();
  if (isOk(all)) {
    console.log(`Total users: ${all.value.length}`);
  }

  // Get specific user
  const fetched = await azoa.api.getUser(reg.value.userId);
  if (isOk(fetched)) {
    console.log("Fetched:", fetched.value.username);
  }

  // Delete
  const deleted = await azoa.api.deleteUser(reg.value.userId);
  if (isOk(deleted)) {
    console.log("User deleted");
  }

  // Logout
  await azoa.auth.logout();
  console.log("Logged out");
}

main().catch(console.error);
