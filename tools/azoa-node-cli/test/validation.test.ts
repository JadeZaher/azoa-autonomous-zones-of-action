import { describe, expect, it } from "vitest";
import { assertValidName } from "../src/validation.js";
import { AzoaCliError } from "../src/errors.js";

describe("assertValidName (H3: every name that becomes a filesystem path component)", () => {
  it.each(["root", "alice-key", "admin_2", "a", "A1_-aB"])("accepts '%s'", (name) => {
    expect(() => assertValidName(name, "--name")).not.toThrow();
  });

  it.each([
    ["", "empty"],
    ["a".repeat(65), "too long (65 chars)"],
    ["../etc/passwd", "path traversal"],
    ["a/b", "contains a slash"],
    ["a\\b", "contains a backslash"],
    ["a b", "contains a space"],
    ["a.key", "contains a dot"],
    ["$(touch pwned)", "shell metacharacters"],
  ])("rejects '%s' (%s)", (name) => {
    expect(() => assertValidName(name, "--name")).toThrow(AzoaCliError);
  });
});
