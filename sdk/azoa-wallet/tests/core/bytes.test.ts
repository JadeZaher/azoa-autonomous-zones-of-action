import { describe, it, expect } from "vitest";
import { toHex, fromHex, concatBytes, equalsBytes } from "../../src/core/bytes.js";

describe("bytes utilities", () => {
  describe("toHex", () => {
    it("converts empty array", () => {
      expect(toHex(new Uint8Array())).toBe("");
    });

    it("converts known bytes", () => {
      expect(toHex(new Uint8Array([0xde, 0xad, 0xbe, 0xef]))).toBe("deadbeef");
    });

    it("zero-pads single digits", () => {
      expect(toHex(new Uint8Array([0, 1, 15]))).toBe("00010f");
    });
  });

  describe("fromHex", () => {
    it("converts empty string", () => {
      expect(fromHex("")).toEqual(new Uint8Array());
    });

    it("converts known hex", () => {
      expect(fromHex("deadbeef")).toEqual(new Uint8Array([0xde, 0xad, 0xbe, 0xef]));
    });

    it("handles uppercase", () => {
      expect(fromHex("DEADBEEF")).toEqual(new Uint8Array([0xde, 0xad, 0xbe, 0xef]));
    });

    it("throws on odd length", () => {
      expect(() => fromHex("abc")).toThrow("Invalid hex string length");
    });

    it("throws on invalid chars", () => {
      expect(() => fromHex("xyz0")).toThrow("Invalid hex character");
    });

    it("roundtrips with toHex", () => {
      const bytes = new Uint8Array([1, 2, 3, 255, 128, 0]);
      expect(fromHex(toHex(bytes))).toEqual(bytes);
    });
  });

  describe("concatBytes", () => {
    it("concatenates arrays", () => {
      const a = new Uint8Array([1, 2]);
      const b = new Uint8Array([3, 4]);
      expect(concatBytes(a, b)).toEqual(new Uint8Array([1, 2, 3, 4]));
    });

    it("handles empty arrays", () => {
      expect(concatBytes(new Uint8Array(), new Uint8Array([1]))).toEqual(new Uint8Array([1]));
    });

    it("handles no arguments", () => {
      expect(concatBytes()).toEqual(new Uint8Array());
    });
  });

  describe("equalsBytes", () => {
    it("returns true for equal arrays", () => {
      expect(equalsBytes(new Uint8Array([1, 2, 3]), new Uint8Array([1, 2, 3]))).toBe(true);
    });

    it("returns false for different lengths", () => {
      expect(equalsBytes(new Uint8Array([1, 2]), new Uint8Array([1, 2, 3]))).toBe(false);
    });

    it("returns false for different values", () => {
      expect(equalsBytes(new Uint8Array([1, 2, 3]), new Uint8Array([1, 2, 4]))).toBe(false);
    });

    it("returns true for empty arrays", () => {
      expect(equalsBytes(new Uint8Array(), new Uint8Array())).toBe(true);
    });
  });
});
