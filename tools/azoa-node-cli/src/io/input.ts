import { createInterface } from "node:readline";

/**
 * Seam between commands and the terminal so tests can drive prompts without
 * a real TTY. Production commands get `createRealInputSource()`; tests pass
 * a `FakeInputSource` (see test/fixtures/fakeInput.ts).
 */
export interface InputSource {
  /** Hidden prompt (passphrase, enrollment token typed interactively). */
  readSecret(label: string): Promise<string>;
  /** Visible prompt (TOTP code, confirmations). */
  readVisible(label: string): Promise<string>;
  /** Non-interactive stdin, trimmed; null when stdin is a TTY (nothing piped). */
  readPipedStdin(): Promise<string | null>;
}

/**
 * Processes one data chunk character-by-character rather than testing the whole chunk against
 * `"\n"`/`"\r"` — a paste that includes its own trailing newline (very common: clipboard content
 * ending in CRLF) arrives as ONE multi-character chunk, and a whole-chunk equality check would
 * never match it, silently appending the newline (and nothing else) to the buffer forever instead
 * of submitting.
 */
function readLineHidden(label: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const stdin = process.stdin;
    const stdout = process.stdout;
    stdout.write(label);
    let buffer = "";
    let settled = false;

    const restoreTerminal = () => {
      stdin.setRawMode?.(false);
      stdin.pause();
      stdin.removeListener("data", onData);
    };

    const onData = (chunk: Buffer) => {
      if (settled) return;
      const str = chunk.toString("utf8");
      for (const ch of str) {
        if (ch === "\n" || ch === "\r" || ch === "\u0004") {
          settled = true;
          restoreTerminal();
          stdout.write("\n");
          resolve(buffer);
          return;
        }
        if (ch === "\u0003") {
          settled = true;
          restoreTerminal(); // Ctrl-C must leave the terminal usable, not stuck in raw mode.
          reject(new Error("Aborted"));
          return;
        }
        if (ch === "\u007f" || ch === "\b") {
          buffer = buffer.slice(0, -1);
          continue;
        }
        buffer += ch;
      }
    };

    stdin.resume();
    stdin.setEncoding("utf8");
    stdin.setRawMode?.(true);
    stdin.on("data", onData);
  });
}

function readLineVisible(label: string): Promise<string> {
  return new Promise((resolve) => {
    const rl = createInterface({ input: process.stdin, output: process.stdout });
    rl.question(label, (answer) => {
      rl.close();
      resolve(answer.trim());
    });
  });
}

// Non-interactive (piped) stdin is shared by every prompt in a single CLI
// invocation — enroll alone needs 4 in order (token, passphrase, confirm,
// TOTP). Two broken approaches were tried and rejected here (join-report.md,
// node-admin-rbac-cli_20261018 P5):
//   1. Slurping the whole stream to EOF per call (the original code here)
//      drains the pipe on the FIRST call and starves every call after it.
//   2. A `readline.Interface` with one `question()` per call looks right but
//      isn't: when multiple newline-terminated answers arrive in a single
//      piped write (the common case — one `printf`/heredoc), Node delivers
//      them as ONE "data" chunk. `question()` only resolves for the first
//      line in that chunk; readline emits the rest as 'line' events with no
//      listener attached (since nothing has called `question()` for them
//      yet), and they are silently dropped — the next read then hangs
//      forever waiting for a line that already came and went.
// The fix: parse lines off `process.stdin` by hand, buffering until a '\n'
// is found, and queue any pending resolver (or any line that arrived before
// its reader asked for it) in strict FIFO order. This survives any chunking.
let stdinBuffer = "";
const queuedLines: string[] = [];
const lineWaiters: Array<(line: string) => void> = [];
let stdinPrimed = false;

function deliverLine(line: string): void {
  const waiter = lineWaiters.shift();
  if (waiter) waiter(line);
  else queuedLines.push(line);
}

function primeStdinLineReader(): void {
  if (stdinPrimed) return;
  stdinPrimed = true;
  process.stdin.setEncoding("utf8");
  process.stdin.on("data", (chunk: string) => {
    stdinBuffer += chunk;
    let newlineIndex: number;
    while ((newlineIndex = stdinBuffer.indexOf("\n")) !== -1) {
      deliverLine(stdinBuffer.slice(0, newlineIndex).replace(/\r$/, ""));
      stdinBuffer = stdinBuffer.slice(newlineIndex + 1);
    }
  });
  process.stdin.on("end", () => {
    if (stdinBuffer.length > 0) {
      deliverLine(stdinBuffer);
      stdinBuffer = "";
    }
    // Stream closed with more reads still waiting (fewer answers piped than
    // prompts issued): resolve with "" rather than hang forever, so a
    // malformed script fails on validation instead of stalling the process.
    while (lineWaiters.length > 0) lineWaiters.shift()!("");
  });
}

function readPipedLine(): Promise<string> {
  primeStdinLineReader();
  const queued = queuedLines.shift();
  if (queued !== undefined) return Promise.resolve(queued.trim());
  return new Promise((resolve) => lineWaiters.push((line) => resolve(line.trim())));
}

export function createRealInputSource(): InputSource {
  return {
    async readSecret(label) {
      if (!process.stdin.isTTY) return readPipedLine();
      return readLineHidden(label);
    },
    async readVisible(label) {
      if (!process.stdin.isTTY) return readPipedLine();
      return readLineVisible(label);
    },
    async readPipedStdin() {
      if (process.stdin.isTTY) return null;
      return readPipedLine();
    },
  };
}
