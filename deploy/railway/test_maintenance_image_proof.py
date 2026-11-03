"""Offline provenance-chain rejection cases; see AGENTS.md."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("maintenance_proof", Path(__file__).with_name("maintenance-image-proof.py"))
proof = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(proof)


class ProvenanceTests(unittest.TestCase):
    def fixture(self, version="v0.2", change=None):
        objects = {}

        def store(kind, value):
            raw = json.dumps(value, separators=(",", ":")).encode()
            digest = "sha256:" + proof.hashlib.sha256(raw).hexdigest()
            objects[(kind, digest)] = raw
            return {"digest": digest, "size": len(raw)}

        config = {"architecture": "amd64", "os": "linux", "config": {"Labels": {
            "org.opencontainers.image.revision": proof.SOURCE,
            "org.opencontainers.image.source": "https://github.com/" + proof.REPOSITORY}}}
        platform = store("manifests", {"config": store("blobs", config)})
        platform["platform"] = {"architecture": "amd64", "os": "linux"}
        material = {"uri": "git+https://github.com/" + proof.REPOSITORY + ".git#" + proof.SOURCE,
                    "digest": {"sha1": proof.SOURCE}}
        if version == "v0.2":
            predicate = {"materials": [material], "buildConfig": {"llbDefinition": ["step"]},
                "invocation": {"configSource": {"entryPoint": "Dockerfile"}},
                "metadata": {"completeness": {"parameters": True, "materials": False}}}
        else:
            predicate = {"buildDefinition": {"resolvedDependencies": [material],
                "internalParameters": {"buildConfig": {"llbDefinition": ["step"]}},
                "externalParameters": {"configSource": {"path": "Dockerfile"}}},
                "runDetails": {"metadata": {"buildkit_completeness": {
                    "request": True, "resolvedDependencies": False}}}}
        statement = {"_type": "https://in-toto.io/Statement/v1",
            "predicateType": "https://slsa.dev/provenance/" + version,
            "subject": [{"digest": {"sha256": platform["digest"].split(":")[1]}}],
            "predicate": predicate}
        if change:
            change(statement, material)
        layer = store("blobs", statement)
        layer["mediaType"] = "application/vnd.in-toto+json"
        attest = store("manifests", {"layers": [layer]})
        attest["annotations"] = {"vnd.docker.reference.type": "attestation-manifest",
                                 "vnd.docker.reference.digest": platform["digest"]}
        index = store("manifests", {"mediaType": "application/vnd.oci.image.index.v1+json",
                                    "manifests": [platform, attest]})
        return index["digest"], objects

    def verify(self, digest, objects):
        def fetch(url, headers, bound):
            if url.startswith("https://ghcr.io/token?"):
                self.assertEqual(headers, {})
                return b'{"token":"synthetic-anonymous-token"}'
            kind, value = url.rsplit("/", 2)[-2:]
            return objects[(kind, value)]
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(proof, "fetch", side_effect=fetch):
                proof.image_proof(digest, Path(directory))
            return json.loads((Path(directory) / "image-proof.json").read_text())

    def test_v02_binds_git_material_with_incomplete_network_dependency_inventory(self):
        digest, objects = self.fixture()
        self.assertEqual(self.verify(digest, objects)["buildSource"], proof.SOURCE)

    def test_v1_binds_git_material_with_max_request(self):
        digest, objects = self.fixture("v1")
        self.assertTrue(self.verify(digest, objects)["maxProvenanceVerified"])

    def test_correct_oci_label_cannot_replace_wrong_git_material(self):
        digest, objects = self.fixture(change=lambda statement, material:
                                      material["digest"].update(sha1="0" * 40))
        with self.assertRaises(ValueError):
            self.verify(digest, objects)

    def test_other_repository_material_cannot_claim_c7(self):
        digest, objects = self.fixture(change=lambda statement, material:
                                      material.update(uri="git+https://github.com/other/repo.git"))
        with self.assertRaises(ValueError):
            self.verify(digest, objects)

    def test_missing_max_build_graph_rejected(self):
        digest, objects = self.fixture(change=lambda statement, material:
                                      statement["predicate"].pop("buildConfig"))
        with self.assertRaises(KeyError):
            self.verify(digest, objects)

    def test_min_request_rejected(self):
        digest, objects = self.fixture(change=lambda statement, material:
            statement["predicate"]["metadata"]["completeness"].update(parameters=False))
        with self.assertRaises(ValueError):
            self.verify(digest, objects)

    def test_provenance_for_other_platform_manifest_rejected(self):
        digest, objects = self.fixture(change=lambda statement, material:
            statement["subject"][0]["digest"].update(sha256="0" * 64))
        with self.assertRaises(ValueError):
            self.verify(digest, objects)

    def test_changed_blob_under_bound_digest_rejected(self):
        digest, objects = self.fixture()
        blob_key = next(key for key in objects if key[0] == "blobs")
        objects[blob_key] += b" "
        with self.assertRaises(ValueError):
            self.verify(digest, objects)

    def test_index_digest_is_content_checked(self):
        digest, objects = self.fixture()
        objects[("manifests", digest)] += b" "
        with self.assertRaises(ValueError):
            self.verify(digest, objects)

    def test_tag_cannot_be_used_as_digest(self):
        with self.assertRaises(ValueError):
            proof.sha("latest")

    def test_private_or_other_redirect_host_rejected(self):
        for url in ("http://ghcr.io/v2/", "https://example.com/blob", "https://user@ghcr.io/blob"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                proof.registry_url(url)

    def test_cdn_redirect_drops_registry_authorization(self):
        request = proof.Request("https://ghcr.io/v2/test/blobs/sha256:a",
                                headers={"Authorization": "Bearer synthetic"})
        redirected = proof.RegistryRedirects().redirect_request(request, None, 302, "Found", {},
            "https://pkg-containers.githubusercontent.com/ghcr1/blobs/example")
        self.assertFalse(redirected.has_header("Authorization"))


class EvidenceReplayTests(unittest.TestCase):
    def fixture(self, directory):
        started = proof.datetime.now(proof.timezone.utc) - proof.timedelta(hours=1)
        self.run = {"id": proof.RUN, "run_attempt": 1, "head_sha": proof.SOURCE,
            "head_branch": proof.VALIDATION_BRANCH, "event": "push", "name": "CI",
            "path": ".github/workflows/ci.yml", "status": "completed", "conclusion": "success",
            "repository": {"full_name": proof.REPOSITORY}, "run_started_at": started.isoformat()}
        self.artifact = {"id": 123, "workflow_run": {"id": self.run["id"], "head_sha": proof.SOURCE},
            "name": "azoa-node-conformance-" + proof.SOURCE, "expired": False,
            "size_in_bytes": 500, "created_at": (started + proof.timedelta(minutes=30)).isoformat()}
        self.directory = Path(directory)

    def verify(self):
        (self.directory / "run.json").write_text(json.dumps(self.run))
        (self.directory / "artifact.json").write_text(json.dumps(self.artifact))
        return proof.metadata(self.directory)

    def test_fresh_source_only_validation_branch_accepted(self):
        with tempfile.TemporaryDirectory() as directory:
            self.fixture(directory)
            self.assertEqual(self.verify()[0]["head_sha"], proof.SOURCE)

    def test_old_main_artifact_is_not_fresh_maintenance_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            self.fixture(directory)
            self.run["head_branch"] = "main"
            with self.assertRaises(ValueError):
                self.verify()

    def test_prior_attempt_artifact_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            self.fixture(directory)
            self.artifact["created_at"] = "2020-01-01T00:00:00Z"
            with self.assertRaises(ValueError):
                self.verify()

    def test_other_run_artifact_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            self.fixture(directory)
            self.artifact["workflow_run"]["id"] = 1234
            with self.assertRaises(ValueError):
                self.verify()

    def test_same_named_workflow_cannot_substitute_other_file(self):
        with tempfile.TemporaryDirectory() as directory:
            self.fixture(directory)
            self.run["path"] = ".github/workflows/other.yml"
            with self.assertRaises(ValueError):
                self.verify()


class SignedInvocationTests(unittest.TestCase):
    def fixture(self, directory, attempt=1, digest=None):
        directory = Path(directory)
        bundle = b"synthetic conformance payload already checked by source-evidence validator"
        (directory / "conformance-evidence.tar.gz").write_bytes(bundle)
        statement = {"predicateType": "https://slsa.dev/provenance/v1",
            "predicate": {"runDetails": {"metadata": {"invocationId":
                "https://github.com/" + proof.REPOSITORY + "/actions/runs/" + str(proof.RUN) +
                "/attempts/" + str(attempt)}}},
            "subject": [{"name": "conformance-evidence.tar.gz", "digest": {
                "sha256": digest or proof.hashlib.sha256(bundle).hexdigest()}}]}
        (directory / "evidence-attestation.json").write_text(json.dumps([
            {"verificationResult": {"statement": statement}}]))
        return directory

    def verify(self, directory):
        with patch.object(proof, "evidence") as checked_source_evidence:
            proof.verified_evidence(directory)
        checked_source_evidence.assert_called_once_with(directory)

    def test_verified_attempt1_invocation_binds_actual_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            self.verify(self.fixture(directory))

    def test_same_run_later_attempt_rejected_even_with_valid_bundle_digest(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                self.verify(self.fixture(directory, attempt=2))

    def test_correct_attempt_cannot_attest_different_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                self.verify(self.fixture(directory, digest="0" * 64))

    def test_unverified_statement_cannot_replace_gh_verification_result(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.fixture(directory)
            result = json.loads((path / "evidence-attestation.json").read_text())
            result[0]["statement"] = result[0].pop("verificationResult")["statement"]
            (path / "evidence-attestation.json").write_text(json.dumps(result))
            with self.assertRaises(KeyError):
                self.verify(path)


if __name__ == "__main__":
    unittest.main()
