// The SDK's HoldCommandView mirrors the C# participant-safe `HoldCommandView` record. This reads the
// actual record source and compares its positional parameters with the interface's keys, so a field
// added, renamed or dropped on either side fails here rather than reaching a consumer as `undefined`.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, it, expect } from "vitest";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = join(HERE, "..", "..", "..", "..");
const CONTRACTS = join(REPO_ROOT, "Models", "Blockchain", "HoldCommandContracts.cs");
const SDK = join(HERE, "hold-commands.ts");

function toCamelCase(name: string): string {
  return name.length === 0 ? name : name[0]!.toLowerCase() + name.slice(1);
}

/** Positional parameter names of `public sealed record HoldCommandView(...)`, attributes and defaults stripped. */
function readRecordFields(source: string): string[] {
  const start = source.indexOf("public sealed record HoldCommandView(");
  expect(start, "the C# contract must still declare a positional HoldCommandView record").toBeGreaterThan(-1);
  const open = source.indexOf("(", start);
  let depth = 0;
  let close = -1;
  for (let index = open; index < source.length; index++) {
    if (source[index] === "(") depth++;
    if (source[index] === ")" && --depth === 0) { close = index; break; }
  }
  expect(close).toBeGreaterThan(open);
  const parameters: string[] = [];
  let current = "";
  depth = 0;
  for (const character of source.slice(open + 1, close)) {
    if (character === "(" || character === "[" || character === "<") depth++;
    if (character === ")" || character === "]" || character === ">") depth--;
    if (character === "," && depth === 0) { parameters.push(current); current = ""; continue; }
    current += character;
  }
  parameters.push(current);
  return parameters
    .map((parameter) => parameter.replace(/\[[^\]]*\]/g, "").split("=")[0]!.trim())
    .filter((parameter) => parameter.length > 0)
    .map((parameter) => toCamelCase(parameter.split(/\s+/).at(-1)!));
}

/** Property names of `export interface HoldCommandView { ... }`. */
function readSdkFields(source: string): string[] {
  const start = source.indexOf("export interface HoldCommandView {");
  expect(start, "the SDK must still declare a HoldCommandView interface").toBeGreaterThan(-1);
  const open = source.indexOf("{", start);
  const close = source.indexOf("\n}", open);
  return source
    .slice(open + 1, close)
    .split(/[;\n]/)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
    .map((entry) => entry.split(/[?:]/)[0]!.trim())
    .filter((name) => /^[A-Za-z][A-Za-z0-9]*$/.test(name));
}

describe("HoldCommandView mirrors the C# command view", () => {
  const recordFields = readRecordFields(readFileSync(CONTRACTS, "utf8"));
  const sdkFields = readSdkFields(readFileSync(SDK, "utf8"));

  it("reads a non-empty field list from both sides", () => {
    expect(recordFields.length).toBeGreaterThan(10);
    expect(sdkFields.length).toBeGreaterThan(10);
  });

  it("declares exactly the fields the server sends", () => {
    expect([...sdkFields].sort()).toEqual([...recordFields].sort());
  });
});
