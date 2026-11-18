import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

from deploy_azure import Deployment, verify_release


COMMIT = "a" * 40
OLD_RELEASE = "step2-workspace"
HOST = "saw2026-test.azurecr.io"
OLD_DIGEST = "sha256:" + "1" * 64
NEW_DIGEST = "sha256:" + "2" * 64
IMAGE_ID = "sha256:" + "3" * 64


class FakeAzure:
    def __init__(self):
        self.calls = []
        self.config = {"image": HOST + "/saw-api:manual", "isMain": True, "targetPort": "8080",
                       "authType": "UserAssigned", "userManagedIdentityClientId": "existing-pull-identity"}

    def __call__(self, args):
        self.calls.append(args)
        if args[:3] == ["az", "acr", "show"]:
            return json.dumps({"loginServer": HOST})
        if args[:4] == ["az", "webapp", "sitecontainers", "show"]:
            return json.dumps(self.config)
        if args[:4] == ["az", "webapp", "sitecontainers", "update"]:
            self.config["image"] = args[args.index("--image") + 1]
            return ""
        if args[:3] == ["az", "webapp", "show"]:
            return json.dumps({"host": "saw-api.example.azurewebsites.net"})
        if args[:4] == ["az", "acr", "repository", "show"]:
            image = args[args.index("--image") + 1]
            return NEW_DIGEST if ":sha-" in image else OLD_DIGEST
        if args[:3] == ["docker", "image", "inspect"]:
            return IMAGE_ID
        if args[:3] in (["az", "acr", "login"], ["az", "webapp", "restart"],
                        ["docker", "image", "load"], ["docker", "image", "tag"],
                        ["docker", "image", "push"]):
            return ""
        raise AssertionError(f"Unexpected external command: {args}")

    def updates(self):
        return [x[x.index("--image") + 1] for x in self.calls
                if x[:4] == ["az", "webapp", "sitecontainers", "update"]]


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        summary = patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": ""})
        summary.start()
        self.addCleanup(summary.stop)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        archive = b"test image archive"
        (self.directory / "image.tar.gz").write_bytes(archive)
        self.build = {"commit": COMMIT, "run_id": "123", "run_attempt": "1", "image_name": "saw-api:ci-" + COMMIT,
                      "image_id": IMAGE_ID, "archive_sha256": hashlib.sha256(archive).hexdigest(),
                      "required_migration": "20261112160000_EnsurePickingHistorySync"}
        (self.directory / "build.json").write_text(json.dumps(self.build), encoding="utf-8")
        self.database = {"status": "verified", "commit": COMMIT, "run_id": "123", "run_attempt": "1",
                         "required_migration": self.build["required_migration"],
                         "after": self.build["required_migration"]}
        self.database_file = self.directory / "migration-result.json"
        self.database_file.write_text(json.dumps(self.database), encoding="utf-8")
        self.azure = FakeAzure()
        self.verify = Mock()
        self.head = Mock(return_value=True)
        self.deployment = Deployment(
            "saw", "saw2026", "saw-api", COMMIT, "123", "1", directory=self.directory,
            record=self.directory / "deployment.json", runner=self.azure, verifier=self.verify,
            head_check=self.head, health_reader=lambda _: {"release": OLD_RELEASE},
            database_record=self.database_file,
        )

    def test_success_deploys_digest_and_preserves_pull_identity(self):
        self.deployment.execute()
        self.assertEqual(self.azure.updates(), [HOST + "/saw-api@" + NEW_DIGEST])
        self.assertEqual(self.azure.config["userManagedIdentityClientId"], "existing-pull-identity")
        self.verify.assert_called_once_with("https://saw-api.example.azurewebsites.net", COMMIT)
        self.assertEqual(self.deployment.record["status"], "deployed")

    def test_corrupt_archive_never_contacts_azure(self):
        (self.directory / "image.tar.gz").write_bytes(b"corrupted")
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.deployment.execute()
        self.assertEqual(self.azure.calls, [])

    def test_other_workflow_run_artifact_is_rejected(self):
        self.build["run_id"] = "999"
        (self.directory / "build.json").write_text(json.dumps(self.build), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "different commit or workflow run"):
            self.deployment.execute()
        self.assertEqual(self.azure.calls, [])

    def test_loaded_image_must_match_tested_identity(self):
        self.build["image_id"] = "sha256:" + "4" * 64
        (self.directory / "build.json").write_text(json.dumps(self.build), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Loaded image differs"):
            self.deployment.execute()
        self.assertEqual(self.azure.updates(), [])
        self.assertFalse(any(x[:3] == ["docker", "image", "push"] for x in self.azure.calls))

    def test_image_requiring_unverified_database_never_touches_azure(self):
        self.build["required_migration"] = "20261201000000_NewSchema"
        (self.directory / "build.json").write_text(json.dumps(self.build), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "database has not been verified"):
            self.deployment.execute()
        self.assertEqual(self.azure.calls, [])

    def test_new_migration_deploys_after_actual_database_verification_without_code_marker(self):
        head = "20261201000000_NewSchema"
        self.build["required_migration"] = head
        self.database.update(required_migration=head, after=head)
        (self.directory / "build.json").write_text(json.dumps(self.build), encoding="utf-8")
        self.database_file.write_text(json.dumps(self.database), encoding="utf-8")
        self.deployment.execute()
        self.assertEqual(self.deployment.record["status"], "deployed")

    def test_failed_or_other_run_database_receipt_blocks_deployment(self):
        for changes in ({"status": "failed"}, {"run_id": "999"}, {"run_attempt": "2"},
                        {"commit": "b" * 40}, {"after": "20261201000000_OtherSchema"}):
            with self.subTest(changes=changes):
                self.database_file.write_text(json.dumps({**self.database, **changes}), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "database has not been verified"):
                    self.deployment.execute()
                self.assertEqual(self.azure.calls, [])

    def test_missing_database_receipt_blocks_deployment(self):
        self.database_file.unlink()
        with self.assertRaises(FileNotFoundError):
            self.deployment.execute()
        self.assertEqual(self.azure.calls, [])

    def test_superseded_commit_never_contacts_azure(self):
        self.head.return_value = False
        self.deployment.execute()
        self.assertEqual(self.azure.calls, [])
        self.assertEqual(self.deployment.record["status"], "superseded")

    def test_main_moves_during_push_app_is_not_changed(self):
        self.head.side_effect = [True, False]
        self.deployment.execute()
        self.assertEqual(self.azure.updates(), [])
        self.assertEqual(self.deployment.record["status"], "superseded")

    def test_bad_release_restores_previous_digest_and_still_fails(self):
        self.verify.side_effect = [RuntimeError("Wrong running release"), None]
        with self.assertRaisesRegex(RuntimeError, "Wrong running release"):
            self.deployment.execute()
        self.assertEqual(self.azure.updates(), [HOST + "/saw-api@" + NEW_DIGEST,
                                               HOST + "/saw-api@" + OLD_DIGEST])
        self.assertEqual(self.deployment.record["status"], "failed")
        self.assertEqual(self.deployment.record["rollback"], "succeeded")
        self.assertEqual(self.verify.call_args.args[1], OLD_RELEASE)

    def test_failed_rollback_is_recorded(self):
        self.verify.side_effect = RuntimeError("Unavailable")
        with self.assertRaisesRegex(RuntimeError, "Unavailable"):
            self.deployment.execute()
        record = json.loads((self.directory / "deployment.json").read_text())
        self.assertEqual(record["status"], "failed")
        self.assertEqual(record["rollback"], "failed")

    def test_rollback_does_not_overwrite_an_external_deployment(self):
        def fail_after_external_change(*_):
            self.azure.config["image"] = HOST + "/saw-api:manual-newer"
            raise RuntimeError("Health failed")
        self.verify.side_effect = fail_after_external_change
        with self.assertRaisesRegex(RuntimeError, "Health failed"):
            self.deployment.execute()
        self.assertEqual(self.azure.updates(), [HOST + "/saw-api@" + NEW_DIGEST])
        self.assertEqual(self.azure.config["image"], HOST + "/saw-api:manual-newer")
        self.assertEqual(self.deployment.record["rollback"], "failed")


class HealthTests(unittest.TestCase):
    def setUp(self):
        self.now = 0

    def tick(self, seconds):
        self.now += seconds

    def test_healthy_old_release_is_not_a_success(self):
        with self.assertRaisesRegex(RuntimeError, "timed out"):
            verify_release("https://example.invalid", COMMIT, timeout=15,
                           fetch=lambda _: {"status": "Healthy", "release": OLD_RELEASE},
                           sleep=self.tick, clock=lambda: self.now)

    def test_live_and_ready_both_must_be_healthy(self):
        with self.assertRaisesRegex(RuntimeError, "timed out"):
            verify_release("https://example.invalid", COMMIT, timeout=15,
                           fetch=lambda url: {"status": "Unhealthy" if url.endswith("ready") else "Healthy",
                                              "release": COMMIT}, sleep=self.tick, clock=lambda: self.now)

    def test_correct_release_passes_after_consecutive_healthy_checks(self):
        verify_release("https://example.invalid", COMMIT, timeout=15,
                       fetch=lambda _: {"status": "Healthy", "release": COMMIT},
                       sleep=self.tick, clock=lambda: self.now)


if __name__ == "__main__":
    unittest.main()
