// OI-86 remediation. The SDK's HoldView is a MIRROR of `HoldController.HoldView`, and the two
// drifted in exactly the way the ledger row was raised about: the C# record dropped Chain and
// AssetId, the TypeScript interface kept them as REQUIRED strings, and a doc-comment above both
// said the shape carried no rail reference. tsc and the SDK suite stayed green because nothing
// reads those fields today — which is why a type-level assertion here would prove nothing.
//
// So this reads the ACTUAL controller source and compares the record's parameter list to the
// interface's key list. It fails when either side gains or loses a field the other does not.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, it, expect } from "vitest";

const HERE = dirname(fileURLToPath(import.meta.url));

/** sdk/azoa-wallet/src/api -> the AZOA repository root. */
const REPO_ROOT = join(HERE, "..", "..", "..", "..");

const CONTROLLER = join(REPO_ROOT, "Controllers", "HoldController.cs");
const CLIENT = join(HERE, "client.ts");

/** Lower-cases the first character the way System.Text.Json's camelCase policy does. */
function toCamelCase(name: string): string {
  return name.length === 0 ? name : name[0]!.toLowerCase() + name.slice(1);
}

/** The positional parameter names of `public sealed record HoldView(...)`, in order. */
function readControllerFields(source: string): string[] {
  const start = source.indexOf("public sealed record HoldView(");
  expect(start, "HoldController must still declare a positional HoldView record").toBeGreaterThan(
    -1,
  );

  const open = source.indexOf("(", start);
  const close = source.indexOf(")", open);
  expect(close).toBeGreaterThan(open);

  return source
    .slice(open + 1, close)
    .split(",")
    .map((parameter) => parameter.trim())
    .filter((parameter) => parameter.length > 0)
    .map((parameter) => parameter.split(/\s+/).at(-1)!)
    .map(toCamelCase);
}

/** The property names of `export interface HoldView { ... }`, in order. */
function readSdkFields(source: string): string[] {
  const start = source.indexOf("export interface HoldView {");
  expect(start, "the SDK must still declare a HoldView interface").toBeGreaterThan(-1);

  const open = source.indexOf("{", start);
  const close = source.indexOf("\n}", open);
  expect(close).toBeGreaterThan(open);

  return source
    .slice(open + 1, close)
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0 && !line.startsWith("//") && !line.startsWith("*"))
    .map((line) => line.split(/[?:]/)[0]!.trim())
    .filter((name) => /^[A-Za-z][A-Za-z0-9]*$/.test(name));
}

describe("HoldView mirrors HoldController", () => {
  const controllerFields = readControllerFields(readFileSync(CONTROLLER, "utf8"));
  const sdkFields = readSdkFields(readFileSync(CLIENT, "utf8"));

  it("reads a non-empty field list from both sides", () => {
    // Without this, every assertion below is vacuously true on a mis-resolved path.
    expect(controllerFields.length).toBeGreaterThan(5);
    expect(sdkFields.length).toBeGreaterThan(5);
  });

  it("declares exactly the fields the controller sends", () => {
    expect([...sdkFields].sort()).toEqual([...controllerFields].sort());
  });

  it("declares neither chain nor assetId on either side", () => {
    // The specific drift OI-86 named: the controller stopped sending these, the mirror kept
    // requiring them, so a consumer that trusted the type got undefined where it typed string.
    expect(controllerFields).not.toContain("chain");
    expect(controllerFields).not.toContain("assetId");
    expect(sdkFields).not.toContain("chain");
    expect(sdkFields).not.toContain("assetId");
  });
});
