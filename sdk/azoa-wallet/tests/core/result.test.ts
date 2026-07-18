import { describe, it, expect } from "vitest";
import { ok, err, isOk, isErr, unwrap, map, mapErr } from "../../src/core/result.js";

describe("Result", () => {
  it("ok creates a success result", () => {
    const result = ok(42);
    expect(result.ok).toBe(true);
    expect(isOk(result)).toBe(true);
    expect(isErr(result)).toBe(false);
  });

  it("err creates a failure result", () => {
    const result = err("something went wrong");
    expect(result.ok).toBe(false);
    expect(isErr(result)).toBe(true);
    expect(isOk(result)).toBe(false);
  });

  it("unwrap returns value for ok", () => {
    expect(unwrap(ok("hello"))).toBe("hello");
  });

  it("unwrap throws for err", () => {
    expect(() => unwrap(err("boom"))).toThrow();
  });

  it("map transforms ok values", () => {
    const result = map(ok(5), (n) => n * 2);
    expect(isOk(result) && result.value).toBe(10);
  });

  it("map passes through err", () => {
    const result = map(err("fail"), (n: number) => n * 2);
    expect(isErr(result) && result.error).toBe("fail");
  });

  it("mapErr transforms err values", () => {
    const result = mapErr(err("fail"), (e) => `wrapped: ${e}`);
    expect(isErr(result) && result.error).toBe("wrapped: fail");
  });

  it("mapErr passes through ok", () => {
    const result = mapErr(ok(42), (e: string) => `wrapped: ${e}`);
    expect(isOk(result) && result.value).toBe(42);
  });
});
