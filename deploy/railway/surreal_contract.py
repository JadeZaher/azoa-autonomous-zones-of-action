#!/usr/bin/env python3
"""Load the reviewed release storage contract; see DEPLOY.md."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
from typing import Any


CONTRACT_PATH = Path(__file__).with_name("surreal-contract.json")
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")


def load_contract(path: Path = CONTRACT_PATH) -> dict[str, Any]:
    contract = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(contract, dict) or set(contract) != {"schema", "version", "image", "platforms", "storage"}:
        raise ValueError("Unexpected Surreal release contract shape")
    if contract["schema"] != "azoa-surreal-release/v1" or contract["version"] != "3.2.0":
        raise ValueError("Unsupported Surreal release version")
    image = contract["image"]
    prefix = "docker.io/surrealdb/surrealdb@"
    if not isinstance(image, str) or not image.startswith(prefix) or DIGEST.fullmatch(image[len(prefix):]) is None:
        raise ValueError("Surreal release image must be an immutable official index")
    platforms = contract["platforms"]
    if (not isinstance(platforms, dict) or set(platforms) != {"linux/amd64"}
            or not isinstance(platforms["linux/amd64"], str)
            or DIGEST.fullmatch(platforms["linux/amd64"]) is None):
        raise ValueError("Unexpected Surreal release platform contract")
    if contract["storage"] != {"engine": "surrealkv", "uri": "surrealkv:///data/db?sync=every",
                                "mountPath": "/data", "sync": "every"}:
        raise ValueError("Surreal release requires persistent SurrealKV with synchronous commits")
    return contract


def admitted_digests(contract: dict[str, Any]) -> frozenset[str]:
    return frozenset((contract["image"].split("@", 1)[1], contract["platforms"]["linux/amd64"]))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--github-env", action="store_true", required=True)
    parser.parse_args()
    contract = load_contract()
    values = {"SURREALDB_IMAGE": contract["image"], "SURREALDB_URI": contract["storage"]["uri"],
              "SURREALDB_VERSION": contract["version"],
              "SURREALDB_PLATFORM_DIGEST": contract["platforms"]["linux/amd64"],
              "SURREALDB_CONTRACT_SHA256": hashlib.sha256(CONTRACT_PATH.read_bytes()).hexdigest()}
    for name, value in values.items():
        print(f"{name}={value}")


if __name__ == "__main__":
    main()
