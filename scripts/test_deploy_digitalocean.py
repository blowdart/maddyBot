"""Offline release checks: no database, DigitalOcean, or Bluesky access."""

import copy
import http.client
import traceback
import unittest
from unittest.mock import patch

import deploy_digitalocean as release


DIGEST = "sha256:" + "a" * 64
VPC_ID = "1a4bd827-491b-4c42-b5a9-6d45c91ffb13"
OTHER_VPC_ID = "6ec5ca5d-ac87-48b4-9ae6-4775c0eb7993"


def app():
    return {
        "spec": {
            "name": "maddybot",
            "vpc": {"id": VPC_ID},
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


def accepted(spec):
    return {"app": {"spec": copy.deepcopy(spec), "pending_deployment": {"id": "new"}}}


def active_responses(spec):
    return [
        {"app": app()},
        {"app": app()},
        accepted(spec),
        {"deployment": {"phase": "ACTIVE", "spec": copy.deepcopy(spec)}},
        {"app": {"spec": copy.deepcopy(spec), "active_deployment": {"id": "new", "spec": copy.deepcopy(spec)}}},
    ]


def invalid_vpcs():
    return (
        None, {}, "", [], [VPC_ID], VPC_ID, 1, True,
        {"id": None}, {"id": ""}, {"id": 1}, {"id": []}, {"id": {}},
        {"id": "existing-vpc"}, {"id": "00000000-0000-0000-0000-000000000000"},
        {"id": " " + VPC_ID}, {"id": VPC_ID + "\n"}, {"id": VPC_ID[:-1]},
        {"id": VPC_ID.replace("-", "")}, {"id": "g" + VPC_ID[1:]},
    )


@patch.dict(release.os.environ, {"GITHUB_STEP_SUMMARY": ""})
class ReleaseTests(unittest.TestCase):
    def assert_update_count(self, api, expected):
        self.assertEqual(expected, sum(len(call.args) == 3 for call in api.call_args_list))

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

    @patch.object(release, "api")
    def test_invalid_or_missing_vpc_never_sends_update(self, api):
        for fetch in (0, 1):
            for missing, vpc in [(True, None)] + [(False, v) for v in invalid_vpcs()]:
                with self.subTest(fetch=fetch, missing=missing, vpc=vpc):
                    target = app()
                    if missing:
                        target["spec"].pop("vpc")
                    else:
                        target["spec"]["vpc"] = vpc
                    api.reset_mock()
                    api.side_effect = ([{"app": app()}] if fetch else []) + [{"app": target}]
                    with self.assertRaisesRegex(RuntimeError, "No update submitted.*spec.vpc"):
                        release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
                    self.assert_update_count(api, 0)

    @patch.object(release, "api")
    def test_configuration_change_during_recheck_never_sends_update(self, api):
        for change in (
            lambda a: a["spec"]["vpc"].update(id=OTHER_VPC_ID),
            lambda a: a["spec"]["vpc"].update(extra={"preserve": True}),
            lambda a: a["spec"]["envs"][0].update(value="changed"),
            lambda a: a["spec"]["services"][0]["image"].update(tag="changed"),
            lambda a: a.update(pending_deployment={"phase": "DEPLOYING"}),
        ):
            with self.subTest(change=change):
                target = app()
                change(target)
                api.reset_mock()
                api.side_effect = [{"app": app()}, {"app": target}]
                with self.assertRaises(RuntimeError):
                    release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
                self.assert_update_count(api, 0)

    @patch.object(release, "api")
    def test_recheck_rejects_removed_or_type_changed_vpc_fields(self, api):
        for vpc in ({"id": VPC_ID}, {"id": VPC_ID, "extra": {"enabled": 1}}):
            with self.subTest(vpc=vpc):
                original = app()
                original["spec"]["vpc"]["extra"] = {"enabled": True}
                target = copy.deepcopy(original)
                target["spec"]["vpc"] = vpc
                api.reset_mock()
                api.side_effect = [{"app": original}, {"app": target}]
                with self.assertRaisesRegex(RuntimeError, "No update submitted.*VPC configuration changed"):
                    release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
                self.assert_update_count(api, 0)

    @patch.object(release, "api")
    def test_invalid_or_missing_spec_never_sends_update(self, api):
        for missing, spec in ((True, None), (False, None), (False, []), (False, "")):
            with self.subTest(missing=missing, spec=spec):
                target = {} if missing else {"spec": spec}
                api.reset_mock()
                api.side_effect = [{"app": target}]
                with self.assertRaisesRegex(RuntimeError, "No update submitted.*spec.vpc"):
                    release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
                self.assert_update_count(api, 0)

    @patch.object(release, "api")
    def test_different_vpcs_and_additional_fields_survive_entire_deployment(self, api):
        for vpc_id in (VPC_ID, OTHER_VPC_ID, OTHER_VPC_ID.upper()):
            with self.subTest(vpc_id=vpc_id):
                target = app()
                target["spec"]["vpc"] = {"id": vpc_id, "extra": {"items": [1, "keep"], "enabled": True}}
                saved = copy.deepcopy(target)
                spec = release.updated_spec(target, "bot", "idunno-registry", DIGEST)
                responses = active_responses(spec)
                responses[0] = {"app": target}
                responses[1] = {"app": copy.deepcopy(target)}
                api.reset_mock()
                api.side_effect = responses
                release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
                body = api.call_args_list[2].args[2]
                self.assertEqual(saved["spec"]["vpc"], body["spec"]["vpc"])
                self.assertEqual({"spec": spec, "update_all_source_versions": False}, body)
                self.assertEqual(saved, target)
                self.assert_update_count(api, 1)

    @patch.object(release, "api")
    def test_prepared_request_must_preserve_fetched_vpc(self, api):
        for vpc in (None, {"id": OTHER_VPC_ID}, {"id": VPC_ID, "extra": True}):
            with self.subTest(vpc=vpc):
                spec = app()["spec"]
                spec["vpc"] = vpc
                api.reset_mock()
                api.side_effect = [{"app": app()}, {"app": app()}]
                with patch.object(release, "updated_spec", return_value=spec):
                    with self.assertRaisesRegex(RuntimeError, "No update submitted: prepared request"):
                        release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
                self.assert_update_count(api, 0)

    @patch.object(release, "api")
    def test_post_write_vpc_failure_never_retries_or_rolls_back(self, api):
        spec = release.updated_spec(app(), "bot", "idunno-registry", DIGEST)
        for location in ("response", "deployment", "current", "active"):
            for missing, vpc in (
                [(True, None)] + [(False, v) for v in invalid_vpcs()]
                + [(False, {"id": OTHER_VPC_ID}), (False, {"id": VPC_ID, "extra": True})]
            ):
                with self.subTest(location=location, missing=missing, vpc=vpc):
                    responses = active_responses(spec)
                    target = {
                        "response": responses[2]["app"]["spec"],
                        "deployment": responses[3]["deployment"]["spec"],
                        "current": responses[4]["app"]["spec"],
                        "active": responses[4]["app"]["active_deployment"]["spec"],
                    }[location]
                    if missing:
                        target.pop("vpc")
                    else:
                        target["vpc"] = vpc
                    api.reset_mock()
                    api.side_effect = responses
                    with self.assertRaisesRegex(RuntimeError, "Update accepted; verification failed"):
                        release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
                    self.assert_update_count(api, 1)

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
        api.side_effect = active_responses(spec)
        release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
        body = api.call_args_list[2].args[2]
        self.assertEqual({"spec": spec, "update_all_source_versions": False}, body)

    @patch.object(release, "api")
    def test_failed_deployment_does_not_report_success(self, api):
        api.side_effect = [
            {"app": app()},
            {"app": app()},
            accepted(app()["spec"]),
            {"deployment": {"phase": "ERROR", "spec": app()["spec"]}},
        ]
        with self.assertRaisesRegex(RuntimeError, "ended in ERROR"):
            release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)

    @patch.object(release, "api")
    def test_lost_update_response_is_not_retried(self, api):
        api.side_effect = [{"app": app()}, {"app": app()}, RuntimeError("Lost response")]
        with self.assertRaisesRegex(RuntimeError, "provider may have accepted it"):
            release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
        self.assertEqual(3, api.call_count)
        self.assert_update_count(api, 1)

    @patch.object(release.urllib.request, "urlopen")
    def test_response_read_failures_warn_without_retrying_put(self, urlopen):
        real_api = release.api
        for error in (
            http.client.IncompleteRead(b"secret-response-fragment", 100),
            ConnectionResetError("secret-response-fragment"),
        ):
            with self.subTest(error=type(error).__name__):
                urlopen.reset_mock()
                urlopen.return_value.__enter__.return_value.read.side_effect = error

                def request(token, path, body=None):
                    if body is None:
                        return {"app": app()}
                    return real_api(token, path, body)

                with patch.object(release, "api", side_effect=request) as api:
                    with self.assertRaisesRegex(RuntimeError, "provider may have accepted it") as caught:
                        release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
                    self.assert_update_count(api, 1)
                    self.assertEqual(3, api.call_count)
                urlopen.assert_called_once()
                self.assertEqual("PUT", urlopen.call_args.args[0].get_method())
                self.assertIn("Inspect App Platform before retrying", str(caught.exception))
                self.assertIn("no automatic retry or rollback", str(caught.exception))
                self.assertNotIn("secret-response-fragment", "".join(traceback.format_exception(caught.exception)))

    @patch.object(release, "api")
    def test_superseded_or_wrong_image_is_not_success(self, api):
        for active in (
            {"id": "different", "spec": app()["spec"]},
            {"id": "new", "spec": app()["spec"]},
        ):
            with self.subTest(active_id=active["id"]):
                api.side_effect = [
                    {"app": app()},
                    {"app": app()},
                    accepted(app()["spec"]),
                    {"deployment": {"phase": "ACTIVE", "spec": app()["spec"]}},
                    {"app": {"spec": app()["spec"], "active_deployment": active}},
                ]
                with self.assertRaises(RuntimeError):
                    release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)

    @patch.object(release.time, "sleep")
    @patch.object(release.time, "monotonic", side_effect=[0, 0, 1201])
    @patch.object(release, "api")
    def test_timeout_is_failure_without_second_update(self, api, monotonic, sleep):
        api.side_effect = [
            {"app": app()},
            {"app": app()},
            accepted(app()["spec"]),
            {"deployment": {"phase": "DEPLOYING", "spec": app()["spec"]}},
        ]
        with self.assertRaises(TimeoutError):
            release.deploy("not-a-token", "app-id", "bot", "idunno-registry", DIGEST)
        self.assertEqual(4, api.call_count)
        self.assert_update_count(api, 1)


if __name__ == "__main__":
    unittest.main()
