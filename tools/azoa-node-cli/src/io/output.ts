export interface OutputSink {
  print(message: string): void;
  printJson(value: unknown): void;
}

export function createOutputSink(json: boolean): OutputSink {
  return {
    print(message: string) {
      if (!json) console.log(message);
    },
    printJson(value: unknown) {
      if (json) console.log(JSON.stringify(value, null, 2));
    },
  };
}

/** Always emits — for errors and for the single JSON payload in --json mode. */
export function emit(json: boolean, humanLine: string, jsonValue: unknown): void {
  if (json) {
    console.log(JSON.stringify(jsonValue, null, 2));
  } else {
    console.log(humanLine);
  }
}
