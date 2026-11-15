import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from release_preflight import validate_pair
import sql_firewall


class PairingTests(unittest.TestCase):
    def test_artifacts_must_have_same_head_and_workflow_attempt(self):
        image = {"commit": "a" * 40, "run_id": "123", "run_attempt": "1", "required_migration": "target"}
        plan = {"Commit": "a" * 40, "RunId": "123", "RunAttempt": "1", "Migrations": ["target"]}
        validate_pair(image, plan)
        for key, value in (("Commit", "b" * 40), ("RunId", "999"),
                           ("RunAttempt", "2"), ("Migrations", ["other"])):
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_pair(image, {**plan, key: value})


class FirewallTests(unittest.TestCase):
    def test_private_ipv6_and_wildcard_addresses_are_not_allowed(self):
        for value in ("127.0.0.1", "10.0.0.1", "0.0.0.0", "::1", "2606:4700:4700::1111"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                sql_firewall.public_ipv4(value)
        self.assertEqual(sql_firewall.public_ipv4("8.8.8.8\n"), "8.8.8.8")

    def test_cleanup_never_deletes_another_runs_rule_or_changed_rule(self):
        with tempfile.TemporaryDirectory() as directory:
            record = Path(directory) / "rule.json"
            rule = {"group": "saw", "server": "saw", "name": "gha-saw-123-1", "ip": "8.8.8.8"}
            record.write_text(json.dumps(rule), encoding="utf-8")
            with patch.object(sql_firewall, "RECORD", record), patch.dict(os.environ, {
                "AZURE_RESOURCE_GROUP": "saw", "AZURE_SQL_SERVER": "saw",
                "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1"
            }), patch.object(sql_firewall, "az") as az:
                az.return_value = json.dumps([{"name": rule["name"], "startIpAddress": "1.1.1.1", "endIpAddress": "1.1.1.1"}])
                with self.assertRaisesRegex(ValueError, "changed externally"):
                    sql_firewall.close_rule()
                self.assertEqual(az.call_count, 1)  # list only; no delete
                rule["name"] = "gha-saw-999-1"
                record.write_text(json.dumps(rule), encoding="utf-8")
                az.reset_mock()
                with self.assertRaisesRegex(ValueError, "another target or workflow"):
                    sql_firewall.close_rule()
                az.assert_not_called()

    def test_cleanup_deletes_only_matching_owned_rule(self):
        with tempfile.TemporaryDirectory() as directory:
            record = Path(directory) / "rule.json"
            record.write_text(json.dumps({"group": "saw", "server": "saw", "name": "gha-saw-123-1", "ip": "8.8.8.8"}))
            with patch.object(sql_firewall, "RECORD", record), patch.dict(os.environ, {
                "AZURE_RESOURCE_GROUP": "saw", "AZURE_SQL_SERVER": "saw",
                "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1"
            }), patch.object(sql_firewall, "az") as az:
                az.side_effect = [json.dumps([{"name": "gha-saw-123-1", "startIpAddress": "8.8.8.8", "endIpAddress": "8.8.8.8"}]), ""]
                sql_firewall.close_rule()
                self.assertEqual(az.call_args.args, ("saw", "saw", "delete", "--name", "gha-saw-123-1", "--output", "none"))


if __name__ == "__main__":
    unittest.main()
