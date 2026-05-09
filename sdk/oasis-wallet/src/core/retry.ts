export interface RetryOptions {
  maxRetries?: number;
  initialDelayMs?: number;
  multiplier?: number;
  isRetryable?: (error: unknown) => boolean;
}

const DEFAULT_OPTIONS: Required<RetryOptions> = {
  maxRetries: 3,
  initialDelayMs: 1000,
  multiplier: 1.5,
  isRetryable: (error: unknown) => {
    if (error instanceof TypeError) return true; // network errors
    if (error instanceof DOMException && error.name === "AbortError") return false;
    return true;
  },
};

export async function withRetry<T>(
  operation: () => Promise<T>,
  options?: RetryOptions
): Promise<T> {
  const opts = { ...DEFAULT_OPTIONS, ...options };
  let retryCount = 0;
  let delayMs = opts.initialDelayMs;

  while (true) {
    try {
      return await operation();
    } catch (error) {
      if (retryCount >= opts.maxRetries || !opts.isRetryable(error)) {
        throw error;
      }
      retryCount++;
      await new Promise((resolve) => setTimeout(resolve, delayMs));
      delayMs = Math.round(delayMs * opts.multiplier);
    }
  }
}
