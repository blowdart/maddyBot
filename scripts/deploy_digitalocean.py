"""Change image selection in a full app spec; never print the secret-bearing spec."""

import copy
import json
import os
import re
import time
import urllib.error
import urllib.request
import uuid


def api(token, path, body=None):
    request = urllib.request.Request(
        "https://api.digitalocean.com/v2" + path,
        data=json.dumps(body).encode() if body is not None else None,
        headers={
            "Authorization": "Bearer " + token,
            "Content-Type": "application/json",
        },
        method="PUT" if body is not None else "GET",
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        # API response bodies may contain configuration; keep them out of Actions logs.
        raise RuntimeError(f"DigitalOcean API returned HTTP {error.code}.") from None
    except urllib.error.URLError:
        raise RuntimeError("Could not reach the DigitalOcean API.") from None


def require_vpc(spec, context, expected=None):
    vpc = spec.get("vpc") if isinstance(spec, dict) else None
    vpc_id = vpc.get("id") if isinstance(vpc, dict) else None
    if (
        not isinstance(vpc_id, str)
        or not re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", vpc_id)
        or uuid.UUID(vpc_id).int == 0
    ):
        raise RuntimeError(
            f"{context}: spec.vpc must contain a valid, nonzero UUID id. "
            "Ensure the deployment token has vpc:read; no VPC will be inferred."
        )
    if expected is not None and json.dumps(vpc, sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise RuntimeError(f"{context}: VPC configuration changed.")
    return vpc


def updated_spec(app, service_name, registry, digest):
    require_vpc(app.get("spec"), "No update submitted")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise ValueError("A pushed SHA-256 image digest is required.")
    for field in ("pending_deployment", "in_progress_deployment"):
        deployment = app.get(field)
        if deployment and deployment.get("phase") not in (
            "ACTIVE", "ERROR", "CANCELED", "SUPERSEDED"
        ):
            raise RuntimeError("Another DigitalOcean deployment is already in progress.")
    spec = copy.deepcopy(app["spec"])
    services = [s for s in spec.get("services", []) if s["name"] == service_name]
    if len(services) != 1:
        raise ValueError("The configured service must match exactly one existing web service.")
    service = services[0]
    image = service.get("image", {})
    if (
        image.get("registry_type") != "DOCR"
        or image.get("repository") != "maddybot"
        or image.get("registry") not in (None, "", registry)
    ):
        raise ValueError("The service must already use the maddybot image in DOCR.")
    if service.get("instance_count") != 1 or service.get("autoscaling"):
        raise ValueError("Production must use one service instance without autoscaling.")
    if image.get("deploy_on_push", {}).get("enabled"):
        raise ValueError("Disable automatic registry push deployments first.")
    if service.get("http_port", 8080) != 8080:
        raise ValueError("Configure the service HTTP port as 8080 before releasing.")
    liveness = service.get("liveness_health_check") or {}
    if (
        liveness.get("http_path") != "/health/live"
        or liveness.get("port", service.get("http_port", 8080)) != 8080
    ):
        raise ValueError("Configure service liveness checks at /health/live on port 8080 before releasing.")
    image.pop("tag", None)
    image["digest"] = digest
    # Explicitly select the known registry, including accounts with multiple registries.
    image["registry"] = registry
    return spec


def deploy(token, app_id, service_name, registry, digest):
    path = "/apps/" + app_id
    app = api(token, path)["app"]
    spec = updated_spec(app, service_name, registry, digest)
    fetched_vpc = copy.deepcopy(app["spec"]["vpc"])
    old_image = next(s["image"] for s in app["spec"]["services"] if s["name"] == service_name)
    previous = json.dumps({
        k: old_image[k] for k in ("registry", "repository", "tag", "digest") if k in old_image
    })
    print("Previous image: " + previous)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write(f"\nPrevious service image: `{previous}`\n")
    # This narrows the race window; DigitalOcean has no atomic conditional spec update.
    latest = api(token, path)["app"]
    updated_spec(latest, service_name, registry, digest)
    require_vpc(latest["spec"], "No update submitted", fetched_vpc)
    if json.dumps(latest["spec"], sort_keys=True) != json.dumps(app["spec"], sort_keys=True):
        raise RuntimeError("No update submitted: app configuration changed while preparing the update.")
    body = {"spec": spec, "update_all_source_versions": False}
    require_vpc(body["spec"], "No update submitted: prepared request", fetched_vpc)
    # No retry of this write: a lost response can still have created a deployment.
    try:
        response = api(token, path, body)
    except (RuntimeError, ValueError, TimeoutError) as error:
        raise RuntimeError(
            "Update request failed; the provider may have accepted it. "
            "Inspect App Platform before retrying; no automatic retry or rollback was submitted."
        ) from error
    result = response["app"]
    verification = "Update accepted; verification failed. Inspect App Platform; no automatic retry or rollback"
    require_vpc(result.get("spec"), verification, fetched_vpc)
    deployment_id = result.get("pending_deployment", {}).get("id")
    if not deployment_id:
        raise RuntimeError(f"{verification}: update returned no deployment ID.")
    print("Deployment ID: " + deployment_id, flush=True)
    deadline = time.monotonic() + 1200
    while time.monotonic() < deadline:
        deployment = api(token, path + "/deployments/" + deployment_id)["deployment"]
        require_vpc(deployment.get("spec"), verification, fetched_vpc)
        phase = deployment["phase"]
        print("Deployment phase: " + phase, flush=True)
        if phase == "ACTIVE":
            current = api(token, path)["app"]
            require_vpc(current.get("spec"), verification, fetched_vpc)
            active = current.get("active_deployment", {})
            require_vpc(active.get("spec"), verification, fetched_vpc)
            if active.get("id") != deployment_id:
                raise RuntimeError(f"{verification}: deployment was superseded.")
            deployed = next(
                s for s in active["spec"]["services"] if s["name"] == service_name
            )
            if deployed["image"].get("digest") != digest:
                raise RuntimeError(f"{verification}: active deployment does not use the requested image digest.")
            if summary:
                with open(summary, "a", encoding="utf-8") as output:
                    output.write(f"\nDeployment `{deployment_id}` is ACTIVE.\n")
            return
        if phase in ("ERROR", "CANCELED", "SUPERSEDED"):
            raise RuntimeError(f"Deployment {deployment_id} ended in {phase}. Inspect App Platform logs.")
        time.sleep(15)
    raise TimeoutError("Deployment did not become ACTIVE within 20 minutes. Inspect App Platform; it may still be running.")


if __name__ == "__main__":
    deploy(
        os.environ["DIGITALOCEAN_ACCESS_TOKEN"],
        os.environ["APP_ID"],
        os.environ["SERVICE_NAME"],
        os.environ["REGISTRY"],
        os.environ["IMAGE_DIGEST"],
    )
