#!/usr/bin/env python3
"""Validate or materialize the operator-gated Railway blueprint."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any


API_PLACEHOLDER = "<PROMOTED_API_IMAGE_REFERENCE>"
FRONTEND_PLACEHOLDER = "<PROMOTED_FRONTEND_IMAGE_REFERENCE>"
IMMUTABLE_IMAGE = re.compile(r"^ghcr\.io/[a-z0-9._/-]+@sha256:[0-9a-f]{64}$")
EXPECTED_SERVICES = ["surrealdb", "azoa-schema", "azoa-api", "azoa-frontend"]
SURREAL_IMAGE = "docker.io/surrealdb/surrealdb@sha256:5757ed157c13b539bdc23a798ba2db1ffba6026deb3d15513058bffc77754a60"


def fail(message: str) -> None:
    raise SystemExit(f"Railway template validation failed: {message}")


def service_map(document: dict[str, Any]) -> dict[str, dict[str, Any]]:
    services = document.get("services")
    if not isinstance(services, list):
        fail("services must be an array")

    names = [service.get("name") for service in services if isinstance(service, dict)]
    if names != EXPECTED_SERVICES:
        fail(f"service order must be {EXPECTED_SERVICES}")
    return {service["name"]: service for service in services}


def validate_contract(document: dict[str, Any], rendered: bool) -> None:
    services = service_map(document)
    for name, service in services.items():
        source = service.get("source")
        if not isinstance(source, dict) or set(source) != {"image"}:
            fail(f"{name} source must contain only one immutable image reference")

    surreal = services["surrealdb"]
    if surreal.get("source", {}).get("image") != SURREAL_IMAGE:
        fail("SurrealDB must remain pinned to the reviewed v3.1.4 OCI index digest")
    if surreal.get("variables", {}).get("SURREAL_SYNC_DATA") != "true":
        fail("SurrealDB must set SURREAL_SYNC_DATA=true")
    start_command = surreal.get("deploy", {}).get("startCommand", "")
    if start_command != "/surreal start":
        fail("SurrealDB must invoke the pinned image executable directly")
    surreal_variables = surreal.get("variables", {})
    if surreal_variables.get("SURREAL_USER") != "azoa_schema_owner":
        fail("SurrealDB owner username must be the explicit non-root schema owner")
    if surreal_variables.get("SURREAL_PASS") != "${{secret(48)}}":
        fail("SurrealDB owner password must use the reviewed 48-character secret slot")
    if surreal_variables.get("PORT") != "8000":
        fail("SurrealDB must expose the fixed private service port 8000")
    if surreal_variables.get("SURREAL_BIND") != "0.0.0.0:8000":
        fail("SurrealDB must bind to the fixed private service port")
    if surreal_variables.get("SURREAL_PATH") != "rocksdb:///data/db":
        fail("SurrealDB must start RocksDB at /data/db")
    if surreal_variables.get("RAILWAY_RUN_UID") != "0":
        fail("SurrealDB must acknowledge Railway's root-owned volume mount")
    if surreal.get("volume", {}).get("mountPath") != "/data":
        fail("SurrealDB volume must be mounted at /data")

    api = services["azoa-api"]
    if api.get("variables", {}).get("DataProtection__KeyRingPath") != "/app/data/data-protection-keys":
        fail("API Data Protection key ring must live on the /app/data volume")
    if api.get("variables", {}).get("RAILWAY_RUN_UID") != "0":
        fail("API entrypoint must repair the Railway volume before dropping privileges")
    if api.get("volume", {}).get("mountPath") != "/app/data":
        fail("API durable volume must be mounted at /app/data")
    if api.get("variables", {}).get("AUTOMAPPER_LICENSE_KEY") != "${{shared.AUTOMAPPER_LICENSE_KEY}}":
        fail("API must consume the sealed shared AutoMapper license variable")
    if api.get("variables", {}).get("AZOA_SKIP_MIGRATIONS") != "1":
        fail("production API must leave schema authority to the one-shot job")
    api_variables = api.get("variables", {})
    forbidden_api_variables = {
        "SURREALFORGE_URL",
        "SURREALFORGE_NS",
        "SURREALFORGE_DB",
        "SURREALFORGE_USER",
        "SURREALFORGE_PASS",
        "SurrealDb__Endpoint",
        "SurrealDb__Namespace",
        "SurrealDb__Database",
        "SurrealDb__User",
        "SurrealDb__Password",
        "AZOA_SKIP_RESET",
    }
    leaked = sorted(forbidden_api_variables.intersection(api_variables))
    if leaked:
        fail(f"API contains legacy or schema-owner variables: {', '.join(leaked)}")
    expected_operator_variables = {
        "NodeOperator__Username": "node-operator",
        "NodeOperator__Password": "${{secret(48)}}",
        "NodeOperator__CredentialRevision": "1",
        "NodeOperator__SessionMinutes": "20",
    }
    for key, expected in expected_operator_variables.items():
        if api_variables.get(key) != expected:
            fail(f"API {key} must preserve the reviewed node-operator seed contract")

    if api_variables.get("Kyc__Provider") != "unavailable":
        fail("Railway KYC must remain fail-closed until an operator configures a provider")
    if api_variables.get("Kyc__ApprovalPolicy__AllowManualInDevelopment") != "false":
        fail("Railway must never enable the Development-only manual KYC adapter")
    expected_kyc_metadata = {
        "Kyc__Hosted__SessionPath": "/sessions",
        "Kyc__Hosted__StatusPath": "/sessions/{sessionId}",
        "Kyc__SubmissionExpiryDays": "30",
        "Kyc__SessionExpiryMinutes": "30",
    }
    for key, expected in expected_kyc_metadata.items():
        if api_variables.get(key) != expected:
            fail(f"API {key} must preserve the reviewed KYC metadata default")
    blank_kyc_configuration = [
        "Kyc__VeriffApiKey",
        "Kyc__VeriffBaseUrl",
        "Kyc__Hosted__ProviderName",
        "Kyc__Hosted__BaseUrl",
        "Kyc__Hosted__ApiKey",
        "Kyc__Hosted__WebhookSecret",
        "Kyc__ApprovalPolicy__PolicyVersion",
        "Kyc__ApprovalPolicy__AssuranceLevel",
    ]
    for key in blank_kyc_configuration:
        if key not in api_variables or api_variables[key] != "":
            fail(f"API {key} must remain an explicit blank host-managed configuration slot")

    schema = services["azoa-schema"]
    if schema.get("deploy", {}).get("startCommand") != "/usr/local/bin/docker-entrypoint.sh schema":
        fail("schema job must use the image's one-shot schema entrypoint")
    if schema.get("deploy", {}).get("restartPolicyType") != "NEVER":
        fail("schema job must remain a one-shot service")
    expected_schema_variables = {
        "ASPNETCORE_ENVIRONMENT": "Production",
        "SURREALFORGE_URL": "${{surrealdb.SURREAL_HTTP_PRIVATE_URL}}",
        "SURREALFORGE_NS": "azoa",
        "SURREALFORGE_DB": "azoa",
        "SURREALFORGE_USER": "${{surrealdb.SURREAL_USER}}",
        "SURREALFORGE_PASS": "${{surrealdb.SURREAL_PASS}}",
        "AZOA_RUNTIME_USER": "azoa_runtime",
        "AZOA_RUNTIME_PASSWORD": "${{secret(48)}}",
    }
    if schema.get("variables") != expected_schema_variables:
        fail("schema job variables must exactly match the owner-to-runtime handoff contract")

    frontend = services["azoa-frontend"]
    if frontend.get("deploy", {}).get("healthcheckPath") != "/api/health":
        fail("frontend Railway health check must use /api/health")
    if frontend.get("variables", {}).get("AZOA_ALLOW_INSECURE_LOCAL_API") is not None:
        fail("local insecure API override must never appear in the Railway blueprint")
    if frontend.get("variables", {}).get("API_URL") != "https://${{azoa-api.RAILWAY_PUBLIC_DOMAIN}}":
        fail("frontend API_URL must be the API's public HTTPS domain")

    expected_images = {
        "azoa-schema": API_PLACEHOLDER,
        "azoa-api": API_PLACEHOLDER,
        "azoa-frontend": FRONTEND_PLACEHOLDER,
    }
    for name, placeholder in expected_images.items():
        image = services[name].get("source", {}).get("image")
        if rendered:
            if not isinstance(image, str) or not IMMUTABLE_IMAGE.fullmatch(image):
                fail(f"{name} must use a whole immutable GHCR name@sha256 reference")
        elif image != placeholder:
            fail(f"{name} source.image must be the whole {placeholder} placeholder")

    if services["azoa-schema"]["source"]["image"] != services["azoa-api"]["source"]["image"]:
        fail("schema job and API must use the exact same immutable image")
    if rendered:
        serialized = json.dumps(document)
        if API_PLACEHOLDER in serialized or FRONTEND_PLACEHOLDER in serialized:
            fail("materialized blueprint still contains an image placeholder")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--template",
        type=Path,
        default=Path(__file__).with_name("template.json"),
    )
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--api-image")
    parser.add_argument("--frontend-image")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    document = json.loads(args.template.read_text(encoding="utf-8"))
    validate_contract(document, rendered=False)
    if args.check:
        if any((args.api_image, args.frontend_image, args.output)):
            fail("--check cannot be combined with rendering arguments")
        return

    if not all((args.api_image, args.frontend_image, args.output)):
        fail("rendering requires --api-image, --frontend-image, and --output")
    if not IMMUTABLE_IMAGE.fullmatch(args.api_image):
        fail("--api-image must be an immutable GHCR name@sha256 reference")
    if not IMMUTABLE_IMAGE.fullmatch(args.frontend_image):
        fail("--frontend-image must be an immutable GHCR name@sha256 reference")

    services = service_map(document)
    services["azoa-schema"]["source"]["image"] = args.api_image
    services["azoa-api"]["source"]["image"] = args.api_image
    services["azoa-frontend"]["source"]["image"] = args.frontend_image
    document["$comment"] = (
        "Materialized by the protected promotion workflow. Deploy in order: surrealdb, "
        "azoa-schema to terminal SUCCESS, azoa-api, then azoa-frontend. The schema job "
        "and API references must remain byte-identical."
    )
    validate_contract(document, rendered=True)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
