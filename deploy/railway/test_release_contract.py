"""Release storage drift and pre-effect refusal tests; see DEPLOY.md."""

import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
sys.path.insert(0, str(HERE))
from surreal_contract import admitted_digests, load_contract


def tool(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


render = tool("release_template", "render-template.py")
rollout = tool("release_rollout", "serial-rollout.py")


def consumers(ci, compose, browser, promotion):
    if (ci.count("surreal_contract.py --github-env") != 4
            or ci.count('"$SURREALDB_IMAGE"') != 3 or ci.count('"$SURREALDB_URI"') != 3
            or "surrealdb/surrealdb:" in ci or "surrealdb/surrealdb@" in ci
            or "rocksdb:///" in ci):
        raise ValueError("CI bypasses the checked-out storage contract")
    if ("${SURREALDB_IMAGE:?" not in compose or "${SURREALDB_URI:?" not in compose
            or "surrealdb/surrealdb:" in compose or "rocksdb:///" in compose):
        raise ValueError("Browser composition bypasses the checked-out storage contract")
    if ("'surreal-contract.json'" not in browser
            or "...settings,\n    ...surrealContractEnvironment()," not in browser):
        raise ValueError("Inherited browser settings override the storage contract")
    if ("surreal_contract.py --github-env" not in promotion
            or '--surreal-image "$SURREALDB_IMAGE"' not in promotion
            or "--surreal-mode already-healthy" not in promotion
            or "inputs.surreal_mode" in promotion or "surrealdb/surrealdb@" in promotion):
        raise ValueError("Normal promotion bypasses the preserve-only contract")


class ReleaseStorageContractTests(unittest.TestCase):
    def setUp(self):
        self.contract = load_contract()
        self.template = json.loads((HERE / "template.json").read_text(encoding="utf-8"))

    def arguments(self, **changes):
        values = {"project-id": "10000000-0000-4000-8000-000000000001",
                  "environment-id": "10000000-0000-4000-8000-000000000002",
                  "surreal-service-id": "10000000-0000-4000-8000-000000000003",
                  "schema-service-id": "10000000-0000-4000-8000-000000000004",
                  "api-service-id": "10000000-0000-4000-8000-000000000005",
                  "frontend-service-id": "10000000-0000-4000-8000-000000000006",
                  "surreal-image": self.contract["image"],
                  "api-image": "ghcr.io/example/api@sha256:" + "1" * 64,
                  "frontend-image": "ghcr.io/example/frontend@sha256:" + "2" * 64,
                  "surreal-mode": "already-healthy",
                  "api-health-url": "https://api.example.test/health",
                  "frontend-health-url": "https://frontend.example.test/api/health"}
        values["confirm"] = f"PROMOTE {values['project-id']}/{values['environment-id']}"
        values.update(changes)
        return ["serial-rollout.py", *[part for key, value in values.items() for part in ("--" + key, value)]]

    def test_contract_rejects_version_engine_sync_and_shape_drift(self):
        changes = [("version", "3.1.4"), ("image", "surrealdb/surrealdb:v3"),
                   ("platforms", {"linux/amd64": "not-a-digest"}),
                   ("storage", {**self.contract["storage"], "engine": "rocksdb"}),
                   ("storage", {**self.contract["storage"], "uri": "surrealkv:///data/db?sync=never"}),
                   ("storage", {**self.contract["storage"], "sync": "never"})]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "contract.json"
            for key, value in changes:
                mutated = copy.deepcopy(self.contract)
                mutated[key] = value
                path.write_text(json.dumps(mutated), encoding="utf-8")
                with self.assertRaises(ValueError):
                    load_contract(path)
            mutated = {**self.contract, "unreviewed": True}
            path.write_text(json.dumps(mutated), encoding="utf-8")
            with self.assertRaises(ValueError):
                load_contract(path)

    def test_template_is_materialized_from_contract_without_authority_drift(self):
        render.validate_contract(self.template, rendered=False)
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "rendered.json"
            arguments = ["render-template.py", "--api-image", "ghcr.io/example/api@sha256:" + "1" * 64,
                         "--frontend-image", "ghcr.io/example/front@sha256:" + "2" * 64, "--output", str(output)]
            with patch.object(sys, "argv", arguments):
                render.main()
            materialized = json.loads(output.read_text(encoding="utf-8"))
            render.validate_contract(materialized, rendered=True)
            services = render.service_map(materialized)
            self.assertEqual(services["surrealdb"]["source"]["image"], self.contract["image"])
            self.assertEqual(services["surrealdb"]["variables"]["SURREAL_PATH"], self.contract["storage"]["uri"])
            self.assertEqual(services["azoa-schema"]["source"], services["azoa-api"]["source"])

    def test_template_image_uri_and_security_drift_are_refused(self):
        for kind in ("image", "uri", "legacy-sync", "owner-leak"):
            mutated = copy.deepcopy(self.template)
            services = render.service_map(mutated)
            if kind == "image":
                services["surrealdb"]["source"]["image"] = "surrealdb/surrealdb:v3.1.4"
            elif kind == "uri":
                services["surrealdb"]["variables"]["SURREAL_PATH"] = "rocksdb:///data/db"
            elif kind == "legacy-sync":
                services["surrealdb"]["variables"]["SURREAL_SYNC_DATA"] = "true"
            else:
                services["azoa-api"]["variables"]["SURREALFORGE_PASS"] = "synthetic-owner-secret"
            with self.assertRaises(SystemExit):
                render.validate_contract(mutated, rendered=False)

    def test_old_mutable_unknown_images_and_redeploy_refuse_before_effects(self):
        cases = [{"surreal-mode": "redeploy"}, {"surreal-image": "surrealdb/surrealdb:v3"},
                 {"surreal-image": "docker.io/surrealdb/surrealdb@sha256:" + "0" * 64},
                 {"surreal-image": "docker.io/surrealdb/surrealdb@sha256:" + "5757ed157c13b539bdc23a798ba2db1ffba6026deb3d15513058bffc77754a60"}]
        for values in cases:
            with (patch.object(sys, "argv", self.arguments(**values)),
                  patch.object(rollout, "RailwayClient") as client,
                  contextlib.redirect_stderr(io.StringIO())):
                with self.assertRaises(SystemExit):
                    rollout.main()
                client.assert_not_called()

    def test_only_approved_index_or_platform_latest_success_is_admitted(self):
        class Client:
            def __init__(inner, digest, status="SUCCESS", image=None):
                inner.digest, inner.status = digest, status
                inner.image = image or self.contract["image"]

            def list_deployments(inner, service):
                return [rollout.Deployment("synthetic-deployment", inner.status, inner.digest)]

            def get_service_image(inner, service):
                return inner.image

        for digest in admitted_digests(self.contract):
            with contextlib.redirect_stdout(io.StringIO()):
                rollout.require_existing_surreal_success(Client(digest), "synthetic", self.contract["image"])
        index = self.contract["image"].split("@", 1)[1]
        for client in (Client("sha256:" + "0" * 64), Client(index, "DEPLOYING"),
                       Client(index, image="surrealdb/surrealdb:v3")):
            with self.assertRaises(rollout.RolloutError):
                rollout.require_existing_surreal_success(client, "synthetic", self.contract["image"])

    def test_preserved_database_then_schema_api_frontend_without_database_mutation(self):
        events = []
        contract = self.contract

        class Client:
            def require_supported_version(inner):
                pass

            def list_deployments(inner, service):
                events.append("database-check")
                return [rollout.Deployment("synthetic-deployment", "SUCCESS", contract["platforms"]["linux/amd64"])]

            def get_service_image(inner, service):
                return contract["image"]

            def configure_image(inner, *args):
                raise AssertionError("The database must never be reconfigured")

        def deploy(client, service, label, *args):
            events.append(label)

        with (patch.object(sys, "argv", self.arguments()), patch.object(rollout.shutil, "which", return_value="fake-cli"),
              patch.object(rollout, "RailwayClient", return_value=Client()),
              patch.object(rollout, "deploy_and_wait", side_effect=deploy),
              patch.object(rollout, "wait_for_health"), contextlib.redirect_stdout(io.StringIO())):
            self.assertEqual(rollout.main(), 0)
        self.assertEqual(events, ["database-check", "azoa-schema", "azoa-api", "azoa-frontend"])

    def test_all_consumers_share_contract_and_bypass_mutations_are_detected(self):
        paths = [".github/workflows/ci.yml", "docker-compose.e2e.yml", "frontend/e2e/scripts/e2e-stack.mjs",
                 ".github/workflows/conformance-release.yml"]
        original = [(ROOT / path).read_text(encoding="utf-8") for path in paths]
        consumers(*original)
        mutations = [(0, original[0].replace("surreal_contract.py --github-env", "bypass.py", 1)),
                     (0, original[0] + "\nSURREALDB_IMAGE: surrealdb/surrealdb:v3.1.4\n"),
                     (1, original[1].replace("${SURREALDB_URI:?", "${UNREVIEWED_URI:?", 1)),
                     (2, original[2].replace("...settings,\n    ...surrealContractEnvironment(),", "...surrealContractEnvironment(),\n    ...settings,", 1)),
                     (3, original[3].replace("--surreal-mode already-healthy", "--surreal-mode redeploy", 1))]
        for index, mutated in mutations:
            inputs = original.copy()
            inputs[index] = mutated
            with self.assertRaises(ValueError):
                consumers(*inputs)


if __name__ == "__main__":
    unittest.main()
