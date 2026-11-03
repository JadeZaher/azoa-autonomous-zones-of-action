#!/usr/bin/env python3
"""Verify maintenance publication inputs and digest-bound source; see AGENTS.md."""
import hashlib
import gzip
import io
import json
import os
from pathlib import Path
import re
import sys
import tarfile
from datetime import datetime, timedelta, timezone
from urllib.parse import urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
import xml.etree.ElementTree as ET
import zipfile

SOURCE = "c7cfa4af82c99e130a26064054ea7c52ab2603bb"
REPOSITORY = "JadeZaher/azoa-autonomous-zones-of-action"
PACKAGE = "jadezaher/azoa-autonomous-zones-of-action"
IMAGE = "ghcr.io/" + PACKAGE
VALIDATION_BRANCH = "codex/c7-storage-validation-20261102"
RUN = 41461162520
ATTEMPT = 1
LIMIT = 2 * 1024 * 1024
MEDIA = ",".join(("application/vnd.oci.image.index.v1+json",
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.docker.distribution.manifest.v2+json"))


def require(condition):
    if not condition:
        raise ValueError("Maintenance proof rejected")


def read_json(path):
    require(path.stat().st_size <= LIMIT)
    return json.loads(path.read_bytes())


def instant(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(parsed.utcoffset() == timedelta(0))
    return parsed


def metadata(directory):
    run = read_json(directory / "run.json")
    artifact = read_json(directory / "artifact.json")
    require(run["id"] == RUN and run["run_attempt"] == ATTEMPT)
    require(run["head_sha"] == SOURCE and run["head_branch"] == VALIDATION_BRANCH)
    require(run["event"] == "push" and run["name"] == "CI")
    require(run["path"] == ".github/workflows/ci.yml")
    require(run["status"] == "completed" and run["conclusion"] == "success")
    require(run["repository"]["full_name"] == REPOSITORY)
    require(artifact["workflow_run"]["id"] == RUN)
    require(artifact["workflow_run"]["head_sha"] == SOURCE)
    require(artifact["name"] == "azoa-node-conformance-" + SOURCE)
    require(artifact["expired"] is False and 0 < artifact["size_in_bytes"] <= LIMIT)
    require(instant(artifact["created_at"]) >= instant(run["run_started_at"]))
    return run, artifact


def evidence(directory):
    run, _ = metadata(directory)
    archive = directory / "evidence.zip"
    require(archive.stat().st_size <= LIMIT)
    with zipfile.ZipFile(archive) as outer:
        entries = outer.infolist()
        require(len(entries) == 1 and entries[0].filename == "conformance-evidence.tar.gz")
        require(0 < entries[0].file_size <= LIMIT and not entries[0].is_dir())
        payload = outer.read(entries[0])
    bundle = directory / "conformance-evidence.tar.gz"
    bundle.write_bytes(payload)
    names = ["metadata.json", "G1.trx", "G2.trx", "G3.trx", "G5.trx", "G7.trx"]
    with gzip.GzipFile(fileobj=io.BytesIO(payload)) as compressed:
        tar_payload = compressed.read(LIMIT + 65537)
        require(len(tar_payload) <= LIMIT + 65536)
    with tarfile.open(fileobj=io.BytesIO(tar_payload), mode="r:") as inner:
        entries = inner.getmembers()
        require([entry.name for entry in entries] == names)
        require(all(entry.isfile() and 0 < entry.size <= 524288 for entry in entries))
        require(sum(entry.size for entry in entries) <= LIMIT)
        contents = {entry.name: inner.extractfile(entry).read(entry.size + 1) for entry in entries}
    info = json.loads(contents["metadata.json"])
    require(set(info) == {"schema", "repository", "commit", "workflow", "runId",
                          "generatedAt", "expiresAt", "files"})
    require(info["schema"] == "azoa-node-conformance-evidence/v1")
    require(info["repository"] == REPOSITORY and info["commit"] == SOURCE)
    require(info["workflow"] == REPOSITORY + "/.github/workflows/ci.yml@refs/heads/" + VALIDATION_BRANCH)
    require(info["runId"] == str(run["id"]))
    generated, expires = instant(info["generatedAt"]), instant(info["expiresAt"])
    require(instant(run["run_started_at"]) <= generated <= datetime.now(timezone.utc) < expires)
    require(generated < expires <= generated + timedelta(hours=24))
    require([entry.get("name") for entry in info["files"]] == names[1:])
    for entry in info["files"]:
        require(set(entry) == {"name", "sha256"})
        trx = contents[entry["name"]]
        require(hashlib.sha256(trx).hexdigest() == entry["sha256"])
        counters = ET.fromstring(trx).find("{*}ResultSummary/{*}Counters")
        require(counters is not None)
        require(int(counters.attrib["executed"]) > 0)
        require(counters.attrib["executed"] == counters.attrib["passed"])
        require(all(int(counters.attrib.get(name, "0")) == 0 for name in
                    ("failed", "error", "timeout", "aborted", "notExecuted")))


def sha(value):
    require(isinstance(value, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", value))
    return value


def verified_evidence(directory):
    evidence(directory)
    verified = read_json(directory / "evidence-attestation.json")
    require(isinstance(verified, list) and verified)
    bundle_digest = hashlib.sha256((directory / "conformance-evidence.tar.gz").read_bytes()).hexdigest()
    invocation = "https://github.com/" + REPOSITORY + "/actions/runs/" + str(RUN) + "/attempts/" + str(ATTEMPT)
    matched = []
    for result in verified:
        statement = result["verificationResult"]["statement"]
        require(statement["predicateType"] == "https://slsa.dev/provenance/v1")
        if statement["predicate"]["runDetails"]["metadata"]["invocationId"] == invocation:
            subjects = statement["subject"]
            require(len(subjects) == 1 and subjects[0]["name"] == "conformance-evidence.tar.gz")
            require(subjects[0]["digest"]["sha256"] == bundle_digest)
            matched.append(statement)
    require(matched)


def registry_url(url):
    parsed = urlsplit(url)
    require(parsed.scheme == "https" and parsed.port in (None, 443))
    require(parsed.username is None and parsed.password is None and not parsed.fragment)
    require(parsed.hostname in {"ghcr.io", "pkg-containers.githubusercontent.com"})
    return parsed


class RegistryRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        destination = registry_url(new_url)
        redirected = super().redirect_request(request, response, code, message, headers, new_url)
        if destination.hostname != "ghcr.io":
            redirected.remove_header("Authorization")
        return redirected


def fetch(url, headers, bound):
    require(registry_url(url).hostname == "ghcr.io")
    with build_opener(RegistryRedirects()).open(Request(url, headers=headers), timeout=30) as response:
        registry_url(response.geturl())
        require(response.status == 200)
        data = response.read(bound + 1)
        require(0 < len(data) <= bound)
        return data


def image_proof(index_digest, directory):
    sha(index_digest)
    auth_url = "https://ghcr.io/token?" + urlencode({
        "service": "ghcr.io", "scope": "repository:" + PACKAGE + ":pull"})
    token = json.loads(fetch(auth_url, {}, LIMIT))["token"]
    require(isinstance(token, str) and token)
    headers = {"Authorization": "Bearer " + token, "Accept": MEDIA}

    def object_at(kind, digest, bound=LIMIT, expected_size=None):
        sha(digest)
        raw = fetch("https://ghcr.io/v2/" + PACKAGE + "/" + kind + "/" + digest, headers, bound)
        require("sha256:" + hashlib.sha256(raw).hexdigest() == digest)
        require(expected_size is None or len(raw) == expected_size)
        return json.loads(raw)

    index = object_at("manifests", index_digest)
    require(index["mediaType"] == "application/vnd.oci.image.index.v1+json")
    manifests = index["manifests"]
    require(len(manifests) == 2)
    images = [item for item in manifests if item.get("platform") ==
              {"architecture": "amd64", "os": "linux"}]
    require(len(images) == 1)
    platform = images[0]
    platform_digest = sha(platform["digest"])
    runtime = object_at("manifests", platform_digest, expected_size=platform["size"])
    config_ref = runtime["config"]
    config = object_at("blobs", config_ref["digest"], expected_size=config_ref["size"])
    require(config["architecture"] == "amd64" and config["os"] == "linux")
    require(config["config"]["Labels"]["org.opencontainers.image.revision"] == SOURCE)
    require(config["config"]["Labels"]["org.opencontainers.image.source"] ==
            "https://github.com/" + REPOSITORY)
    attestations = [item for item in manifests if item.get("annotations", {}).get(
        "vnd.docker.reference.type") == "attestation-manifest"]
    require(len(attestations) == 1)
    attestation = attestations[0]
    require(attestation["annotations"]["vnd.docker.reference.digest"] == platform_digest)
    statement_manifest = object_at("manifests", attestation["digest"], expected_size=attestation["size"])
    require(len(statement_manifest["layers"]) == 1)
    layer = statement_manifest["layers"][0]
    require(layer["mediaType"] == "application/vnd.in-toto+json")
    statement = object_at("blobs", layer["digest"], bound=16 * LIMIT, expected_size=layer["size"])
    require(statement["_type"] in {"https://in-toto.io/Statement/v1", "https://in-toto.io/Statement/v0.1"})
    require(len(statement["subject"]) == 1)
    require(statement["subject"][0]["digest"]["sha256"] == platform_digest.split(":")[1])
    predicate = statement["predicate"]
    if statement["predicateType"] == "https://slsa.dev/provenance/v0.2":
        materials = predicate["materials"]
        require(predicate["buildConfig"])
        require(predicate["invocation"]["configSource"]["entryPoint"] == "Dockerfile")
        require(predicate["metadata"]["completeness"]["parameters"] is True)
    else:
        require(statement["predicateType"] == "https://slsa.dev/provenance/v1")
        definition = predicate["buildDefinition"]
        materials = definition["resolvedDependencies"]
        require(definition["internalParameters"]["buildConfig"])
        require(definition["externalParameters"]["configSource"]["path"] == "Dockerfile")
        complete = predicate["runDetails"]["metadata"]["buildkit_completeness"]
        require(complete["request"] is True)
    allowed = {prefix + "https://github.com/" + REPOSITORY + suffix
               for prefix in ("", "git+") for suffix in
               (".git#" + SOURCE, "#" + SOURCE, ".git", "")}
    git_materials = [item for item in materials if item.get("digest", {}).get("sha1") == SOURCE]
    require(len(git_materials) == 1 and git_materials[0]["uri"] in allowed)
    proof = {"anonymousPublicPull": True, "image": IMAGE + "@" + index_digest,
        "indexDigest": index_digest, "platformDigest": platform_digest,
        "buildSource": SOURCE, "gitMaterial": git_materials[0],
        "buildKitProvenanceDigest": layer["digest"], "dockerfile": "Dockerfile",
        "maxProvenanceVerified": True, "platform": "linux/amd64"}
    (directory / "image-proof.json").write_text(json.dumps(proof, indent=2) + "\n")


def receipt(index_digest, directory):
    sha(index_digest)
    run, artifact = metadata(directory)
    verified_evidence(directory)
    proof = read_json(directory / "image-proof.json")
    require(proof["indexDigest"] == index_digest and proof["buildSource"] == SOURCE)
    workflow_sha = os.environ["GITHUB_SHA"]
    require(re.fullmatch(r"[0-9a-f]{40}", workflow_sha))
    for name in ("image-attestation.json", "evidence-attestation.json"):
        require(read_json(directory / name))
    output = {"schema": "azoa-storage-maintenance-publication/v1",
        "image": IMAGE + "@" + index_digest, "buildSource": SOURCE,
        "publicationWorkflowRevision": workflow_sha,
        "publicationRunId": os.environ["GITHUB_RUN_ID"],
        "publicationRunAttempt": os.environ["GITHUB_RUN_ATTEMPT"],
        "sourceCiRunId": run["id"], "sourceCiRunAttempt": ATTEMPT,
        "sourceCiArtifactId": artifact["id"],
        "sourceCiAttemptStartedAt": run["run_started_at"],
        "sourceCiSignedInvocation": "https://github.com/" + REPOSITORY + "/actions/runs/" + str(RUN) + "/attempts/" + str(ATTEMPT),
        "anonymousPublicPull": True, "tagPublished": False,
        "deploymentPerformed": False, "paymentPromotion": False,
        "mainPromotionCertificationClaimed": False}
    (directory / "publication-receipt.json").write_text(json.dumps(output, indent=2) + "\n")


if __name__ == "__main__":
    try:
        command = sys.argv[1]
        if command in {"evidence-metadata", "evidence", "evidence-attestation"}:
            require(len(sys.argv) == 3)
            directory = Path(sys.argv[2])
            {"evidence-metadata": metadata, "evidence": evidence,
             "evidence-attestation": verified_evidence}[command](directory)
        else:
            require(len(sys.argv) == 4 and command in {"image", "receipt"})
            (image_proof if command == "image" else receipt)(sys.argv[2], Path(sys.argv[3]))
        print(json.dumps({"proofStep": command, "verified": True}))
    except Exception:
        print(json.dumps({"verified": False, "details": "Maintenance proof rejected; no raw response printed."}))
        sys.exit(1)
