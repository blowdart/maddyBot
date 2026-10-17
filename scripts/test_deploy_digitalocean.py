"""Offline release checks: no database, DigitalOcean, or Bluesky access."""

import copy
import unittest
from unittest.mock import patch

import deploy_digitalocean as release


DIGEST = "sha256:" + "a" * 64


def app():
    return {
        "spec": {
            "name": "maddybot",
            "vpc": {"id": "existing-vpc"},
            "databases": [{"name": "existing-database"}],
            "envs": [{"key": "secret", "value": "encrypted-value"}],
            "ingress": {"rules": [{"component": {"name": "bot"}, "match": {"path": {"prefix": "/"}}}]},
            "workers": [{"name": "unrelated-worker", "image": {"tag": "untouched"}}],
            "services": [{
                "name": "bot",
                "instance_count": 1,
                "http_port": 8080,
                "image": {"registry_type": "DOCR", "repository": "maddybot", "tag": "old"},
                "envs": [{"key": "service-secret", "value": "encrypted-value"}],
                "health_check": {"http_path": "/health/live", "port": 8080},
                "liveness_health_check": {"http_path": "/health/live", "port": 8080},
                "termination": {"grace_period_seconds": 60},
            }],
        }
    }


@patch.dict(release.os.environ, {"GITHUB_STEP_SUMMARY": ""})
class ReleaseTests(unittest.TestCase):
    def test_preserves_everything_except_image_selection(self):
        original = app()
        original["spec"]["services"].append({"name": "other", "image": {"tag": "untouched"}})
        saved = copy.deepcopy(original)
        actual = release.updated_spec(original, "bot", "idunno-registry", DIGEST)
        expected = copy.deepcopy(original["spec"])
        expected["services"][0]["image"] = {
            "registry_type": "DOCR", "repository": "maddybot",
            "registry": "idunno-registry", "digest": DIGEST,
        }
        self.assertEqual(expected, actual)
        self.assertEqual(saved, original)

    def test_rejects_unsafe_targets(self):
        for change in (
            lambda a: a.update(pending_deployment={"phase": "DEPLOYING"}),
            lambda a: a["spec"]["services"][0].update(instance_count=2),
            lambda a: a["spec"]["services"][0].update(autoscaling={"min_instance_count": 1, "max_instance_count": 2}),
            lambda a: a["spec"]["services"][0].update(name="wrong"),
            lambda a: a["spec"]["services"].append(copy.deepcopy(a["spec"]["services"][0])),
            lambda a: a["spec"].pop("services"),
            lambda a: a["spec"]["services"][0].pop("liveness_health_check"),
            lambda a: a["spec"]["services"][0]["liveness_health_check"].update(http_path="/health/dependencies"),
            lambda a: a["spec"]["services"][0]["liveness_health_check"].update(port=80),
            lambda a: a["spec"]["services"][0].update(http_port=80),
            lambda a: a["spec"]["services"][0]["image"].update(repository="other"),
            lambda a: a["spec"]["services"][0]["image"].update(registry="other"),
            lambda a: a["spec"]["services"][0]["image"].update(deploy_on_push={"enabled": True}),
        ):
            with self.subTest(change=change):
                target = app()
                change(target)
                with self.assertRaises((ValueError, RuntimeError)):
                    release.updated_spec(target, "bot", "idunno-registry", DIGEST)

    def test_rejects_invalid_digest(self):
        with self.assertRaises(ValueError):
            release.updated_spec(app(), "bot", "idunno-registry", "latest")

    def test_accepts_default_http_and_liveness_ports(self):
        target = app()
        target["spec"]["services"][0].pop("http_port")
        target["spec"]["services"][0]["liveness_health_check"].pop("port")
        actual = release.updated_spec(target, "bot", "idunno-registry", DIGEST)
        self.assertEqual(DIGEST, actual["services"][0]["image"]["digest"])

    def test_worker_with_matching_name_is_not_a_service(self):
        target = app()
        target["spec"]["workers"] = target["spec"].pop("services")
        with self.assertRaisesRegex(ValueError, "exactly one existing web service"):
            release.updated_spec(target, "bot", "idunno-registry", DIGEST)

    @patch.object(release, "api")
    def test_waits_for_exact_active_deployment(self, api):
        spec = release.updated_spec(app(), "bot", "idunno-registry", DIGEST)
        api.side_effect = [
            {"app": app()},
            {"app": {"pending_deployment": {"id": "new"}}},
            {"deployment": {"phase": "ACTIVE"}},
            {"app": {"active_deployment": {"id": "new", "spec": spec}}},
        ]
        release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
        body = api.call_args_list[1].args[2]
        self.assertEqual({"spec": spec, "update_all_source_versions": False}, body)

    @patch.object(release, "api")
    def test_failed_deployment_does_not_report_success(self, api):
        api.side_effect = [
            {"app": app()},
            {"app": {"pending_deployment": {"id": "new"}}},
            {"deployment": {"phase": "ERROR"}},
        ]
        with self.assertRaisesRegex(RuntimeError, "ended in ERROR"):
            release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)

    @patch.object(release, "api")
    def test_lost_update_response_is_not_retried(self, api):
        api.side_effect = [{"app": app()}, RuntimeError("Lost response")]
        with self.assertRaisesRegex(RuntimeError, "Lost response"):
            release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
        self.assertEqual(2, api.call_count)

    @patch.object(release, "api")
    def test_superseded_or_wrong_image_is_not_success(self, api):
        for active in (
            {"id": "different", "spec": app()["spec"]},
            {"id": "new", "spec": app()["spec"]},
        ):
            with self.subTest(active_id=active["id"]):
                api.side_effect = [
                    {"app": app()},
                    {"app": {"pending_deployment": {"id": "new"}}},
                    {"deployment": {"phase": "ACTIVE"}},
                    {"app": {"active_deployment": active}},
                ]
                with self.assertRaises(RuntimeError):
                    release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)

    @patch.object(release.time, "sleep")
    @patch.object(release.time, "monotonic", side_effect=[0, 0, 1201])
    @patch.object(release, "api")
    def test_timeout_is_failure_without_second_update(self, api, monotonic, sleep):
        api.side_effect = [
            {"app": app()},
            {"app": {"pending_deployment": {"id": "new"}}},
            {"deployment": {"phase": "DEPLOYING"}},
        ]
        with self.assertRaises(TimeoutError):
            release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
        self.assertEqual(3, api.call_count)


if __name__ == "__main__":
    unittest.main()
