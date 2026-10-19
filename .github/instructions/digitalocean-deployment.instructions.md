---
applyTo: 'scripts/**,.github/scripts/**,.github/workflows/**,**/*deploy*.py,**/*deploy*.ps1,**/*deploy*.sh,**/*deploy*.yml,**/*deploy*.yaml'
description: 'DigitalOcean deployment permissions, VPC preservation, and live-service safety.'
---

# DigitalOcean deployment safety

Treat the target as a live service. Apply these rules when writing deployment
scripts, planning releases, advising on permissions, or investigating failures.

## Token permissions

Keep registry publication and app deployment permissions distinct. MaddyBot's
`.github/workflows/release.yml` currently uses the same `DIGITALOCEAN_ACCESS_TOKEN`
secret in the GitHub `release` environment for both operations; it therefore
needs the union of the lists below. Do not introduce separate tokens or change
secrets or permissions without user authorization.

The complete registry-publication scope list is:

- `registry:read`
- `registry:update`

For the existing image-based App Platform update flow, the baseline deployment
scope list is:

- `app:update`
- `app:read`
- `regions:read`
- `sizes:read`
- `actions:read`

Add `database:read` when the app spec references a Managed Database, and
`vpc:read` when the app uses a VPC. MaddyBot uses both, so its complete
deployment scope list is:

- `app:update`
- `app:read`
- `regions:read`
- `sizes:read`
- `actions:read`
- `database:read`
- `vpc:read`

For the current shared `release` token, always give the complete combined list:
`registry:read`, `registry:update`, `app:update`, `app:read`, `regions:read`,
`sizes:read`, `actions:read`, `database:read`, and `vpc:read`.

MaddyBot's runtime database connection is configured through
`ConnectionStrings:Bot` (`ConnectionStrings__Bot` in production). The deployment
script copies the live app spec, including its `databases` bindings; the offline
fixture in `scripts/test_deploy_digitalocean.py` checks that they are preserved.
The repository does not contain the production app spec or a production
database hostname. Preserve the Managed Database reference and require
`database:read` for its visibility. A public database hostname is not a reason
to remove the VPC safeguard or `vpc:read`.

Check the actual app configuration and current official endpoint documentation
for additional requirements; the baseline is not universally sufficient.
Do not grant `database:view_credentials`, `vpc:update`, database write/delete,
or registry create/delete speculatively. Explain any demonstrated need for
additional permissions before requesting them. Scopes are team-wide, not
restricted to this app.

Always repeat the complete scope list for the specific token being discussed.
Do not make the user combine partial lists or scroll back. Token scopes cannot
be edited in place; changing them requires a replacement token. GitHub secret
values cannot be retrieved after saving. Never ask the user to recover an old
secret or paste credentials into chat.

## Full-spec updates and VPC loss

An App Platform PUT containing `spec` replaces the full app configuration.
Changing only image fields in memory does not make the request an image-only
patch. `update_all_source_versions: false` does not change replacement semantics.
Do not claim a narrower image-selection endpoint exists without verifying it.

DigitalOcean can omit `spec.vpc` from app and deployment GET responses when the
token lacks `vpc:read`. Submitting that incomplete spec disconnects the app from
its VPC. This behavior caused a confirmed JerryChart production outage, not a
MaddyBot incident.

For a VPC-connected app:

- Reject missing, null, malformed, empty, or zero-UUID `spec.vpc.id` before any write.
- Preserve the complete fetched VPC object unchanged.
- Never hard-code a VPC ID, infer a default, or reconstruct missing configuration from a historical deployment.
- Require the current app and active deployment to expose consistent VPC identities before updating; missing required visibility must fail closed.
- Re-fetch immediately before writing and reject concurrent configuration changes.
- Verify the prepared request preserves the VPC and changes only intended image fields.
- Preserve database bindings, encrypted secrets, internal-only networking, health checks, commands, instance settings, ingress, and all other unrelated configuration.
- Keep full app specs in memory; never log or upload them.

MaddyBot's release updates an existing web service, not a worker. Its name comes
from the `release` environment variable `DIGITALOCEAN_SERVICE_NAME` (documented
as `maddybot` for this deployment). Preserve the workflow's `APP_ID` and
`REGISTRY` settings and the `maddybot` image repository; do not infer new targets.
The entry point is `scripts/deploy_digitalocean.py`, with offline coverage in
`scripts/test_deploy_digitalocean.py`.

A read/recheck narrows the race window but is not an atomic lock. Do not claim
conditional-update protection unless the provider supports it. Operators must
avoid simultaneous control-panel edits.

Do not assume rollback restores effective VPC attachment. During the confirmed
JerryChart incident, a rollback deployment spec contained the VPC but did not
restore the app's effective networking.

## Uncertain writes and verification

Submit the update once. Never automatically retry a PUT or submit a compensating
rollback. A timeout, connection reset, truncated response, malformed JSON, or
missing response fields can occur after the provider accepted the update.

Wrap the single write and response extraction so ordinary exceptions produce
sanitized guidance that the update may have been accepted, the operator must
inspect App Platform Networking and Activity before retrying, and no automatic
retry or rollback was submitted. Suppress sensitive exception content and
chained tracebacks. This narrow boundary must report failure explicitly, not
hide it or return success.

Identify the deployment associated with the update, wait for its terminal
state, and verify the expected image selection and configuration, including
VPC retention. Distinguish pre-write rejection, uncertain write outcome,
accepted-update verification failure, and verified `ACTIVE` deployment.
Post-write checks cannot prevent or undo an already accepted unsafe update.

Do not rely on a particular service/worker rollout order. `ACTIVE` does not
prove background processing is healthy: inspect runtime logs, durable progress,
heartbeat freshness, and ingestion lag separately.

## Regression coverage and operations

Add offline tests proving:

- Missing or malformed VPC responses cause zero PUTs.
- Changed pre-write configuration causes zero PUTs.
- Different valid VPC IDs and additional VPC fields are preserved.
- Only intended image fields change.
- Truncated/reset update responses produce uncertain-outcome guidance and exactly one PUT.
- Sensitive response content is not logged.

Run relevant tests on Windows and Linux. Offline tests do not guarantee provider
behavior or a safe live rollout; state that limitation.

Do not deploy, restart, alter networking, change secrets, dispatch workflows,
or rerun them without user authorization. Follow the repository's failed
GitHub Actions evidence-preservation protocol before a rerun: retain the
original run ID, attempt, commit SHA, job/check identities, logs, annotations,
and artifacts, and diagnose the original failure or explicitly identify what
remains unknown. A successful retry does not explain or fix the earlier failure.

An old workflow rerun uses its original workflow/source commit and does not
acquire safeguards merged afterward. Use a new run from the corrected commit
when those safeguards are required.

Use evidence, not assumptions. Do not invent missing scopes, blame credentials
without evidence, or promise a deployment cannot break.
