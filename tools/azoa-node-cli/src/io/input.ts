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

function readLineHidden(label: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const stdin = process.stdin;
    const stdout = process.stdout;
    stdout.write(label);
    const onData = (char: Buffer) => {
      const str = char.toString("utf8");
      if (str === "\n" || str === "\r" || str === "\u0004") {
        stdin.setRawMode?.(false);
        stdin.pause();
        stdin.removeListener("data", onData);
        stdout.write("\n");
        resolve(buffer);
        return;
      }
      if (str === "\u0003") {
        stdin.removeListener("data", onData);
        reject(new Error("Aborted"));
        return;
      }
      if (str === "\u007f" || str === "\b") {
        buffer = buffer.slice(0, -1);
        return;
      }
      buffer += str;
    };
    let buffer = "";
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

async function readAllStdin(): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of process.stdin) {
    chunks.push(Buffer.from(chunk));
  }
  return Buffer.concat(chunks).toString("utf8").trim();
}

export function createRealInputSource(): InputSource {
  return {
    async readSecret(label) {
      if (!process.stdin.isTTY) return readAllStdin();
      return readLineHidden(label);
    },
    async readVisible(label) {
      return readLineVisible(label);
    },
    async readPipedStdin() {
      if (process.stdin.isTTY) return null;
      return readAllStdin();
    },
  };
}
