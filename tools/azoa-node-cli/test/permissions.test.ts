import { describe, expect, it } from "vitest";
import { parseIcaclsAces, verifyAcl, type WindowsIdentity } from "../src/storage/permissions.js";

const PATH = "C:\\Users\\atooz\\.azoa\\keys\\root.key";

// Real `icacls <path>` output shapes (H2): first line is "<path> <principal>:(<flags>)...",
// continuation ACEs are indented to align under the ACE (not the path), then a blank line and a
// "Successfully processed ..." summary.
const SINGLE_ACE_SECURED = `${PATH} ATOOZ-PC\\atooz:(F)\n\nSuccessfully processed 1 files; Failed processing 0 files\n`;

const SINGLE_ACE_INHERITED = `${PATH} ATOOZ-PC\\atooz:(I)(F)\n\nSuccessfully processed 1 files; Failed processing 0 files\n`;

const MULTI_ACE_INHERITED = [
  `${PATH} BUILTIN\\Administrators:(I)(F)`,
  `                                    NT AUTHORITY\\SYSTEM:(I)(F)`,
  `                                    ATOOZ-PC\\atooz:(I)(F)`,
  `                                    BUILTIN\\Users:(I)(RX)`,
  ``,
  `Successfully processed 1 files; Failed processing 0 files`,
  ``,
].join("\n");

const SINGLE_ACE_WRONG_PRINCIPAL = `${PATH} BUILTIN\\Administrators:(F)\n\nSuccessfully processed 1 files; Failed processing 0 files\n`;

const SINGLE_ACE_DOMAIN_FORM = `${PATH} CORP\\atooz:(F)\n\nSuccessfully processed 1 files; Failed processing 0 files\n`;

const SINGLE_ACE_READONLY = `${PATH} ATOOZ-PC\\atooz:(RX)\n\nSuccessfully processed 1 files; Failed processing 0 files\n`;

const SINGLE_ACE_MULTI_FLAG_NOT_INHERITED = `${PATH} ATOOZ-PC\\atooz:(OI)(CI)(F)\n\nSuccessfully processed 1 files; Failed processing 0 files\n`;

const IDENTITY: WindowsIdentity = { nameCandidates: ["atooz", "ATOOZ-PC\\atooz"], sid: "S-1-5-21-111-222-333-1001" };

describe("parseIcaclsAces (pure, table-tested against real icacls output)", () => {
  it("parses a single secured ACE", () => {
    expect(parseIcaclsAces(SINGLE_ACE_SECURED, PATH)).toEqual([{ principal: "ATOOZ-PC\\atooz", flags: ["F"] }]);
  });

  it("parses multiple inherited ACEs on continuation lines", () => {
    expect(parseIcaclsAces(MULTI_ACE_INHERITED, PATH)).toEqual([
      { principal: "BUILTIN\\Administrators", flags: ["I", "F"] },
      { principal: "NT AUTHORITY\\SYSTEM", flags: ["I", "F"] },
      { principal: "ATOOZ-PC\\atooz", flags: ["I", "F"] },
      { principal: "BUILTIN\\Users", flags: ["I", "RX"] },
    ]);
  });

  it("parses multiple flags on one ACE (OI)(CI)(F) without treating it as inherited", () => {
    expect(parseIcaclsAces(SINGLE_ACE_MULTI_FLAG_NOT_INHERITED, PATH)).toEqual([
      { principal: "ATOOZ-PC\\atooz", flags: ["OI", "CI", "F"] },
    ]);
  });

  it("throws when the output doesn't start with the expected path", () => {
    expect(() => parseIcaclsAces(SINGLE_ACE_SECURED, "C:\\different\\path.key")).toThrow(/did not start with/);
  });
});

describe("verifyAcl (H2: the real check, not a path-substring match)", () => {
  it("passes for exactly one non-inherited Full Control ACE for the current user", () => {
    expect(() => verifyAcl(SINGLE_ACE_SECURED, PATH, IDENTITY)).not.toThrow();
  });

  it("matches a DOMAIN\\username form against the identity's name candidates", () => {
    const domainIdentity: WindowsIdentity = { nameCandidates: ["atooz", "CORP\\atooz"] };
    expect(() => verifyAcl(SINGLE_ACE_DOMAIN_FORM, PATH, domainIdentity)).not.toThrow();
  });

  it("matches by SID when the ACE principal is a raw SID", () => {
    const sidAce = `${PATH} S-1-5-21-111-222-333-1001:(F)\n\nSuccessfully processed 1 files; Failed processing 0 files\n`;
    expect(() => verifyAcl(sidAce, PATH, IDENTITY)).not.toThrow();
  });

  it("rejects multiple ACEs even if one of them is correct", () => {
    expect(() => verifyAcl(MULTI_ACE_INHERITED, PATH, IDENTITY)).toThrow(/found 4/);
  });

  it("rejects a surviving inherited (I) flag — /inheritance:r silently failed", () => {
    expect(() => verifyAcl(SINGLE_ACE_INHERITED, PATH, IDENTITY)).toThrow(/inherited/);
  });

  it("rejects a grant that is missing Full Control", () => {
    expect(() => verifyAcl(SINGLE_ACE_READONLY, PATH, IDENTITY)).toThrow(/does not grant Full Control/);
  });

  it(
    "rejects a principal that doesn't match the current user, even though the file PATH itself " +
      "contains the username as a substring (H2 regression: the old check matched on path text, not the ACE)",
    () => {
      expect(PATH.toLowerCase()).toContain("atooz"); // the trap the old implementation fell into
      expect(() => verifyAcl(SINGLE_ACE_WRONG_PRINCIPAL, PATH, IDENTITY)).toThrow(/does not match the current user/);
    },
  );
});
