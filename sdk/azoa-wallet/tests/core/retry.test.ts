import { describe, it, expect, vi } from "vitest";
import { withRetry } from "../../src/core/retry.js";

describe("withRetry", () => {
  it("returns value on first success", async () => {
    const fn = vi.fn().mockResolvedValue("ok");
    const result = await withRetry(fn, { maxRetries: 3, initialDelayMs: 1 });
    expect(result).toBe("ok");
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("retries on failure then succeeds", async () => {
    const fn = vi.fn()
      .mockRejectedValueOnce(new TypeError("network"))
      .mockResolvedValue("recovered");

    const result = await withRetry(fn, { maxRetries: 3, initialDelayMs: 1 });
    expect(result).toBe("recovered");
    expect(fn).toHaveBeenCalledTimes(2);
  });

  it("throws after max retries exhausted", async () => {
    const fn = vi.fn().mockRejectedValue(new TypeError("network"));
    await expect(
      withRetry(fn, { maxRetries: 2, initialDelayMs: 1 })
    ).rejects.toThrow("network");
    expect(fn).toHaveBeenCalledTimes(3); // 1 initial + 2 retries
  });

  it("does not retry non-retryable errors", async () => {
    const fn = vi.fn().mockRejectedValue(new Error("fatal"));
    await expect(
      withRetry(fn, {
        maxRetries: 3,
        initialDelayMs: 1,
        isRetryable: () => false,
      })
    ).rejects.toThrow("fatal");
    expect(fn).toHaveBeenCalledTimes(1);
  });
});
