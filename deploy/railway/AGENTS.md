# Railway maintenance publication verification

`maintenance-image-proof.py` validates exact, bounded CI artifacts and public OCI
metadata. `test_maintenance_image_proof.py` contains offline adversarial cases;
run it with `python3 -m unittest discover -s deploy/railway -p
'test_maintenance_image_proof.py'` only after the authoring batch is frozen.

Source CI identity is deliberately distinct from publication workflow identity.
The source-only validation ref points to exact c7 and runs unchanged ci.yml.
The publication ref has reviewed tooling W while its BuildKit Git context is c7.
The helper checks exact repo/branch/SHA/event/workflow/run/attempt and artifact
creation time before bounded ZIP/tar parsing, signed metadata freshness and TRX
hash/counter checks. `gh attestation verify` independently checks signatures and
source/workflow identities; the helper then requires the verified statement's
signed invocation URL to equal CI41461162520/attempts/1 and its subject digest to
equal the actual bundle. Later-attempt artifacts cannot borrow the original
attempt's start time. The helper never claims to implement cryptography.

The public OCI verifier obtains an anonymous read token, checks the index,
platform/config, provenance-manifest and statement bytes against every parent
digest and size, and requires linux/amd64 and exact c7 Git material. Supported
SLSA0.2/v1 shapes require the maximum build graph and complete request parameters.
BuildKit cannot enumerate every package fetched inside RUN, so a false dependency
completeness flag is not treated as proof failure or reinterpreted as reproducible
dependencies. Floating .NET bases, apt and restore inputs remain a new build.

Only HTTPS GHCR and its specific public blob CDN are accepted. Registry
authorization is removed when redirected to the CDN; error bodies/tokens are
never printed. A separate Docker pull uses a new empty Docker config, avoiding
the workflow's authenticated registry credentials. No image is executed here.

The helper's success is an input to review, not permission to deploy or change
package visibility. No helper performs provider operations, retrieves production
variables, reads database rows or accesses protected key material. The final
candidate still requires independent source/image review and SAME9098 continuity
and storage/restart safeguards before any Railway deployment.

The publication-only job uses no GitHub environment. The observed environment404
does not prove reviewer protection, and this job must not implicitly create an
environment or invent a required-reviewer configuration prerequisite. Root/C
explicit review of W and authorized manual dispatch gate this maintenance
publication; normal main promotion policy remains a separate contract.
