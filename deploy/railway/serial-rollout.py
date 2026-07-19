#!/usr/bin/env python3
"""Promote an already-configured Azoa Railway environment in dependency order."""

from __future__ import annotations

import argparse
import ipaddress
import json
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
from dataclasses import dataclass
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


RAILWAY_CALLER = "skill:use-railway@1.3.5"
TRANSIENT_STATUSES = frozenset(
    {"INITIALIZING", "QUEUED", "BUILDING", "DEPLOYING", "WAITING"}
)
FAILURE_STATUSES = frozenset(
    {"FAILED", "CRASHED", "REMOVED", "REMOVING", "CANCELLED", "SKIPPED"}
)
MINIMUM_RAILWAY_VERSION = (5, 27, 0)
IMMUTABLE_IMAGE = re.compile(
    r"^[a-z0-9][a-z0-9._/-]*@(?P<digest>sha256:[0-9a-f]{64})$"
)


class RolloutError(RuntimeError):
    """A release gate failed closed."""


class NoRedirectHandler(HTTPRedirectHandler):
    """Keep health probes pinned to the operator-confirmed origin."""

    def redirect_request(
        self,
        request: Request,
        file_pointer: Any,
        code: int,
        message: str,
        headers: Any,
        new_url: str,
    ) -> None:
        return None


@dataclass(frozen=True)
class Deployment:
    id: str
    status: str
    image_digest: str | None


@dataclass(frozen=True)
class RolloutConfig:
    project_id: str
    environment_id: str
    surreal_service_id: str
    schema_service_id: str
    api_service_id: str
    frontend_service_id: str
    surreal_image: str
    api_image: str
    frontend_image: str
    surreal_mode: str
    api_health_url: str
    frontend_health_url: str
    deployment_timeout_seconds: int
    health_timeout_seconds: int
    poll_seconds: int
    probe_timeout_seconds: int


class RailwayClient:
    def __init__(self, executable: str, project_id: str, environment_id: str) -> None:
        self._executable = executable
        self._project_id = project_id
        self._environment_id = environment_id
        self._environment = os.environ.copy()
        self._environment["RAILWAY_CALLER"] = RAILWAY_CALLER
        self._environment["RAILWAY_AGENT_SESSION"] = (
            f"azoa-serial-rollout-{uuid.uuid4()}"
        )

    def require_supported_version(self) -> None:
        try:
            result = subprocess.run(
                [self._executable, "--version"],
                check=False,
                capture_output=True,
                text=True,
                timeout=10,
                env=self._environment,
            )
        except (subprocess.TimeoutExpired, OSError) as exc:
            raise RolloutError("Railway CLI version could not be verified") from exc

        match = re.search(r"\b(\d+)\.(\d+)\.(\d+)\b", result.stdout)
        if result.returncode != 0 or match is None:
            raise RolloutError("Railway CLI returned an unsupported version response")
        version = tuple(int(part) for part in match.groups())
        if version < MINIMUM_RAILWAY_VERSION:
            required = ".".join(str(part) for part in MINIMUM_RAILWAY_VERSION)
            raise RolloutError(f"Railway CLI {required} or newer is required")

    def list_deployments(self, service_id: str) -> list[Deployment]:
        payload = self._run_json(
            [
                "deployment",
                "list",
                "--project",
                self._project_id,
                "--environment",
                self._environment_id,
                "--service",
                service_id,
                "--limit",
                "20",
                "--json",
            ],
            "deployment list",
        )
        if not isinstance(payload, list):
            raise RolloutError("deployment list returned an unexpected JSON shape")

        deployments: list[Deployment] = []
        for item in payload:
            if not isinstance(item, dict):
                raise RolloutError("deployment list returned an invalid record")
            deployment_id = item.get("id")
            status = item.get("status")
            if not isinstance(deployment_id, str) or not isinstance(status, str):
                raise RolloutError("deployment list omitted a required field")
            meta = item.get("meta")
            image_digest = meta.get("imageDigest") if isinstance(meta, dict) else None
            if image_digest is not None and not isinstance(image_digest, str):
                raise RolloutError("deployment list returned an invalid image digest")
            deployments.append(
                Deployment(deployment_id, status.upper(), image_digest)
            )
        return deployments

    def configure_image(self, service_id: str, image: str, label: str) -> None:
        payload = self._run_json(
            [
                "environment",
                "edit",
                "--project",
                self._project_id,
                "--environment",
                self._environment_id,
                "--service-config",
                service_id,
                "source.image",
                image,
                "--message",
                f"Promote {label} immutable image",
                "--json",
            ],
            "image promotion",
        )
        if not isinstance(payload, dict):
            raise RolloutError("image promotion returned an unexpected JSON shape")

    def get_service_image(self, service_id: str) -> str:
        payload = self._run_json(
            [
                "environment",
                "config",
                "--project",
                self._project_id,
                "--environment",
                self._environment_id,
                "--json",
            ],
            "environment config",
        )
        services = payload.get("services") if isinstance(payload, dict) else None
        service = services.get(service_id) if isinstance(services, dict) else None
        source = service.get("source") if isinstance(service, dict) else None
        image = source.get("image") if isinstance(source, dict) else None
        if not isinstance(image, str) or not image:
            raise RolloutError("environment config omitted the service image")
        return image

    def _run_json(self, arguments: list[str], operation: str) -> Any:
        try:
            result = subprocess.run(
                [self._executable, *arguments],
                check=False,
                capture_output=True,
                text=True,
                timeout=60,
                env=self._environment,
            )
        except subprocess.TimeoutExpired as exc:
            raise RolloutError(f"{operation} timed out") from exc
        except OSError as exc:
            raise RolloutError(f"{operation} could not start Railway CLI") from exc

        if result.returncode != 0:
            raise RolloutError(
                f"{operation} failed with Railway CLI exit code {result.returncode}"
            )

        output = result.stdout.strip().lstrip("\ufeff")
        try:
            return json.loads(output)
        except json.JSONDecodeError as exc:
            raise RolloutError(f"{operation} returned invalid JSON") from exc


def parse_arguments() -> tuple[RolloutConfig, str]:
    parser = argparse.ArgumentParser(
        description=(
            "Promote immutable Railway images serially: SurrealDB gate, schema, "
            "API, then frontend."
        )
    )
    parser.add_argument("--project-id", required=True)
    parser.add_argument("--environment-id", required=True)
    parser.add_argument("--surreal-service-id", required=True)
    parser.add_argument("--schema-service-id", required=True)
    parser.add_argument("--api-service-id", required=True)
    parser.add_argument("--frontend-service-id", required=True)
    parser.add_argument("--surreal-image", required=True)
    parser.add_argument("--api-image", required=True)
    parser.add_argument("--frontend-image", required=True)
    parser.add_argument(
        "--surreal-mode",
        choices=("redeploy", "already-healthy"),
        required=True,
        help=(
            "redeploy the configured SurrealDB source and wait for Railway's /health "
            "gate, or attest that the existing deployment already passed that gate"
        ),
    )
    parser.add_argument("--api-health-url", required=True)
    parser.add_argument("--frontend-health-url", required=True)
    parser.add_argument("--confirm", required=True)
    parser.add_argument(
        "--deployment-timeout-seconds", type=int, default=900, metavar="SECONDS"
    )
    parser.add_argument(
        "--health-timeout-seconds", type=int, default=180, metavar="SECONDS"
    )
    parser.add_argument("--poll-seconds", type=int, default=5, metavar="SECONDS")
    parser.add_argument(
        "--probe-timeout-seconds", type=int, default=5, metavar="SECONDS"
    )
    arguments = parser.parse_args()

    try:
        project_id = canonical_uuid(arguments.project_id, "project")
        environment_id = canonical_uuid(arguments.environment_id, "environment")
        service_ids = {
            "surreal": canonical_uuid(arguments.surreal_service_id, "SurrealDB service"),
            "schema": canonical_uuid(arguments.schema_service_id, "schema service"),
            "api": canonical_uuid(arguments.api_service_id, "API service"),
            "frontend": canonical_uuid(
                arguments.frontend_service_id, "frontend service"
            ),
        }
        if len(set(service_ids.values())) != len(service_ids):
            raise ValueError("service IDs must be distinct")

        images = {
            "surreal": immutable_image(arguments.surreal_image, "SurrealDB"),
            "api": immutable_image(arguments.api_image, "API/schema"),
            "frontend": immutable_image(arguments.frontend_image, "frontend"),
        }

        api_health_url = validate_health_url(
            arguments.api_health_url, "/health", "API"
        )
        frontend_health_url = validate_health_url(
            arguments.frontend_health_url, "/api/health", "frontend"
        )
        validate_range(
            arguments.deployment_timeout_seconds, 30, 3600, "deployment timeout"
        )
        validate_range(arguments.health_timeout_seconds, 15, 900, "health timeout")
        validate_range(arguments.poll_seconds, 1, 60, "poll interval")
        validate_range(arguments.probe_timeout_seconds, 1, 30, "probe timeout")
        if arguments.probe_timeout_seconds > arguments.health_timeout_seconds:
            raise ValueError("probe timeout cannot exceed health timeout")
    except ValueError as exc:
        parser.error(str(exc))

    expected_confirmation = f"PROMOTE {project_id}/{environment_id}"
    if arguments.confirm != expected_confirmation:
        parser.error(f'--confirm must exactly equal "{expected_confirmation}"')

    return (
        RolloutConfig(
            project_id=project_id,
            environment_id=environment_id,
            surreal_service_id=service_ids["surreal"],
            schema_service_id=service_ids["schema"],
            api_service_id=service_ids["api"],
            frontend_service_id=service_ids["frontend"],
            surreal_image=images["surreal"],
            api_image=images["api"],
            frontend_image=images["frontend"],
            surreal_mode=arguments.surreal_mode,
            api_health_url=api_health_url,
            frontend_health_url=frontend_health_url,
            deployment_timeout_seconds=arguments.deployment_timeout_seconds,
            health_timeout_seconds=arguments.health_timeout_seconds,
            poll_seconds=arguments.poll_seconds,
            probe_timeout_seconds=arguments.probe_timeout_seconds,
        ),
        expected_confirmation,
    )


def canonical_uuid(value: str, label: str) -> str:
    try:
        return str(uuid.UUID(value))
    except (ValueError, AttributeError) as exc:
        raise ValueError(f"{label} ID must be a UUID") from exc


def immutable_image(value: str, label: str) -> str:
    if IMMUTABLE_IMAGE.fullmatch(value) is None:
        raise ValueError(f"{label} image must be an immutable name@sha256 reference")
    return value


def image_digest(value: str) -> str:
    match = IMMUTABLE_IMAGE.fullmatch(value)
    if match is None:
        raise RolloutError("invalid immutable image reference")
    return match.group("digest")


def validate_range(value: int, minimum: int, maximum: int, label: str) -> None:
    if value < minimum or value > maximum:
        raise ValueError(f"{label} must be between {minimum} and {maximum} seconds")


def validate_health_url(value: str, expected_path: str, label: str) -> str:
    try:
        parsed = urlsplit(value)
        port = parsed.port
    except ValueError as exc:
        raise ValueError(f"{label} health URL is invalid") from exc

    if parsed.scheme.lower() != "https" or not parsed.hostname:
        raise ValueError(f"{label} health URL must use public HTTPS")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError(f"{label} health URL cannot include credentials or parameters")
    if port not in (None, 443):
        raise ValueError(f"{label} health URL must use the standard HTTPS port")
    if parsed.path != expected_path:
        raise ValueError(f"{label} health URL path must be exactly {expected_path}")

    hostname = parsed.hostname.lower().rstrip(".")
    if (
        hostname == "localhost"
        or hostname.endswith(".localhost")
        or hostname.endswith(".internal")
        or hostname.endswith(".local")
        or "." not in hostname
    ):
        raise ValueError(f"{label} health URL must use a public hostname")
    try:
        address = ipaddress.ip_address(hostname)
    except ValueError:
        pass
    else:
        if not address.is_global:
            raise ValueError(f"{label} health URL must use a public IP address")

    return value


def require_existing_surreal_success(
    client: RailwayClient, service_id: str, expected_image: str
) -> None:
    deployments = client.list_deployments(service_id)
    expected_digest = image_digest(expected_image)
    if (
        not deployments
        or deployments[0].status != "SUCCESS"
        or deployments[0].image_digest != expected_digest
        or client.get_service_image(service_id) != expected_image
    ):
        raise RolloutError(
            "SurrealDB precondition failed: source or successful deployment digest drifted"
        )
    print("[surrealdb] pinned digest and operator-attested Railway /health gate are valid")


def deploy_and_wait(
    client: RailwayClient,
    service_id: str,
    label: str,
    timeout_seconds: int,
    poll_seconds: int,
    image: str,
) -> None:
    baseline_ids = {deployment.id for deployment in client.list_deployments(service_id)}
    expected_digest = image_digest(image)
    client.configure_image(service_id, image, label)
    deployment_id: str | None = None
    deadline = time.monotonic() + timeout_seconds
    last_status: str | None = None

    while time.monotonic() < deadline:
        deployments = client.list_deployments(service_id)
        candidates = [item for item in deployments if item.id not in baseline_ids]
        if len(candidates) > 1:
            raise RolloutError(
                f"{label} has concurrent new deployments; target is ambiguous"
            )
        if deployment_id is None and candidates:
            deployment_id = candidates[0].id
        elif deployment_id is not None and any(
            item.id != deployment_id for item in candidates
        ):
            raise RolloutError(
                f"{label} received another deployment during the release"
            )

        current = next(
            (item for item in deployments if item.id == deployment_id), None
        )
        status = current.status if current else "WAITING_FOR_DEPLOYMENT"
        if status != last_status:
            print(f"[{label}] {status}")
            last_status = status

        if status == "SUCCESS":
            if current is None or current.image_digest != expected_digest:
                raise RolloutError(
                    f"{label} reached SUCCESS with an unexpected image digest"
                )
            if client.get_service_image(service_id) != image:
                raise RolloutError(
                    f"{label} service image drifted after deployment"
                )
            return
        if status in FAILURE_STATUSES:
            raise RolloutError(f"{label} reached terminal status {status}")
        if status not in TRANSIENT_STATUSES and status != "WAITING_FOR_DEPLOYMENT":
            raise RolloutError(f"{label} returned unknown deployment status {status}")
        sleep_until_next_poll(deadline, poll_seconds)

    raise RolloutError(f"{label} did not reach SUCCESS before its timeout")


def wait_for_health(
    url: str,
    label: str,
    expected_service: str,
    health_timeout_seconds: int,
    probe_timeout_seconds: int,
    poll_seconds: int,
) -> None:
    deadline = time.monotonic() + health_timeout_seconds
    opener = build_opener(NoRedirectHandler())
    request = Request(
        url,
        method="GET",
        headers={
            "Accept": "application/json",
            "User-Agent": "azoa-serial-rollout/1",
        },
    )

    while time.monotonic() < deadline:
        try:
            with opener.open(request, timeout=probe_timeout_seconds) as response:
                payload_bytes = response.read(65_537)
                if (
                    200 <= response.status < 300
                    and len(payload_bytes) <= 65_536
                    and health_payload_matches(payload_bytes, expected_service)
                ):
                    print(f"[{label}] public health gate passed")
                    return
        except (HTTPError, URLError, TimeoutError, OSError, json.JSONDecodeError):
            pass
        sleep_until_next_poll(deadline, poll_seconds)

    raise RolloutError(f"{label} public health gate did not pass before its timeout")


def health_payload_matches(payload_bytes: bytes, expected_service: str) -> bool:
    payload = json.loads(payload_bytes)
    if not isinstance(payload, dict):
        return False

    if expected_service == "azoa-frontend":
        checks = payload.get("checks")
        api_check = checks.get("api") if isinstance(checks, dict) else None
        return (
            payload.get("service") == "azoa-frontend"
            and payload.get("status") == "ready"
            and isinstance(api_check, dict)
            and api_check.get("ready") is True
        )

    if expected_service == "azoa-api":
        checks = payload.get("checks")
        if payload.get("status") not in {"Healthy", "Degraded"} or not isinstance(
            checks, list
        ):
            return False
        return any(
            isinstance(check, dict)
            and check.get("name") == "storage-db"
            and check.get("status") == "Healthy"
            for check in checks
        )

    return False


def sleep_until_next_poll(deadline: float, poll_seconds: int) -> None:
    remaining = deadline - time.monotonic()
    if remaining > 0:
        time.sleep(min(poll_seconds, remaining))


def main() -> int:
    config, _ = parse_arguments()
    executable = shutil.which("railway")
    if executable is None:
        raise RolloutError("Railway CLI is not installed or is not on PATH")

    client = RailwayClient(executable, config.project_id, config.environment_id)
    client.require_supported_version()

    if config.surreal_mode == "redeploy":
        deploy_and_wait(
            client,
            config.surreal_service_id,
            "surrealdb",
            config.deployment_timeout_seconds,
            config.poll_seconds,
            config.surreal_image,
        )
    else:
        require_existing_surreal_success(
            client, config.surreal_service_id, config.surreal_image
        )

    deploy_and_wait(
        client,
        config.schema_service_id,
        "azoa-schema",
        config.deployment_timeout_seconds,
        config.poll_seconds,
        config.api_image,
    )
    deploy_and_wait(
        client,
        config.api_service_id,
        "azoa-api",
        config.deployment_timeout_seconds,
        config.poll_seconds,
        config.api_image,
    )
    wait_for_health(
        config.api_health_url,
        "azoa-api",
        "azoa-api",
        config.health_timeout_seconds,
        config.probe_timeout_seconds,
        config.poll_seconds,
    )
    deploy_and_wait(
        client,
        config.frontend_service_id,
        "azoa-frontend",
        config.deployment_timeout_seconds,
        config.poll_seconds,
        config.frontend_image,
    )
    wait_for_health(
        config.frontend_health_url,
        "azoa-frontend",
        "azoa-frontend",
        config.health_timeout_seconds,
        config.probe_timeout_seconds,
        config.poll_seconds,
    )
    print("Azoa serial rollout completed: every release gate passed.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except RolloutError as error:
        print(f"rollout blocked: {error}", file=sys.stderr)
        raise SystemExit(1) from error
    except KeyboardInterrupt:
        print("rollout interrupted; no later service was started", file=sys.stderr)
        raise SystemExit(130)
