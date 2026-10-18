"""Compare INFO FOR TABLE of every table between two SurrealDB databases (CI schema job; .github/AGENTS.md §schema-upgrade)."""
import base64
import json
import sys
import urllib.request

ENDPOINT, NAMESPACE = "http://127.0.0.1:8030", "azoa"


def query(database, sql):
    request = urllib.request.Request(
        f"{ENDPOINT}/sql", data=sql.encode(), method="POST",
        headers={"Surreal-NS": NAMESPACE, "Surreal-DB": database, "Accept": "application/json",
                 "Authorization": "Basic " + base64.b64encode(b"root:root").decode()})
    result = json.load(urllib.request.urlopen(request))[0]
    if result.get("status") != "OK":
        raise SystemExit(f"{database}: {sql} -> {result}")
    return result["result"]


def snapshot(database):
    tables = query(database, "INFO FOR DB;")["tables"]
    return {name: query(database, f"INFO FOR TABLE {name};") for name in sorted(tables) if name != "schema_migration"}


def main(candidate, reference, label):
    left, right = snapshot(candidate), snapshot(reference)
    differences = [name for name in sorted(set(left) | set(right)) if left.get(name) != right.get(name)]
    for name in differences:
        print(f"::error::{name} differs between {label} and Generated/Schemas")
        print(json.dumps({"candidate": left.get(name), "generated": right.get(name)}, indent=1, sort_keys=True))
    print(f"Compared {len(right)} table(s); {len(differences)} difference(s).")
    return 1 if differences else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else sys.argv[1]))
