import type { InputSource } from "../../src/io/input.js";

type Scripted = string | (() => string);

function resolve(value: Scripted): string {
  return typeof value === "function" ? value() : value;
}

/**
 * Scripted InputSource for tests — no real TTY involved. Entries may be a
 * plain string or a `() => string` thunk for values that can't be known
 * until the server has responded (e.g. a TOTP code for a secret the fake
 * server only just generated).
 */
export class FakeInputSource implements InputSource {
  private secrets: Scripted[];
  private visibles: Scripted[];
  private piped: string | null;

  constructor(options: { secrets?: Scripted[]; visibles?: Scripted[]; piped?: string | null } = {}) {
    this.secrets = [...(options.secrets ?? [])];
    this.visibles = [...(options.visibles ?? [])];
    this.piped = options.piped ?? null;
  }

  async readSecret(): Promise<string> {
    const value = this.secrets.shift();
    if (value === undefined) throw new Error("FakeInputSource: no scripted secret left");
    return resolve(value);
  }

  async readVisible(): Promise<string> {
    const value = this.visibles.shift();
    if (value === undefined) throw new Error("FakeInputSource: no scripted visible answer left");
    return resolve(value);
  }

  async readPipedStdin(): Promise<string | null> {
    const value = this.piped;
    this.piped = null;
    return value;
  }
}
