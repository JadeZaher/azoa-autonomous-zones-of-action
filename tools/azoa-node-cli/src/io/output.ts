/** Always emits — for errors and for the single JSON payload in --json mode. */
export function emit(json: boolean, humanLine: string, jsonValue: unknown): void {
  if (json) {
    console.log(JSON.stringify(jsonValue, null, 2));
  } else {
    console.log(humanLine);
  }
}
