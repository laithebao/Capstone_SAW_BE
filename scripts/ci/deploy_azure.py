"""Deploy an already-tested image to the existing sidecar-enabled App Service.

Uses only the Python standard library. Azure credentials come from azure/login.
Does not fetch app settings, run migrations, or change registry authentication.
"""

import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from urllib.request import Request, urlopen


def run(command):
    result = subprocess.run(command, text=True, capture_output=True, check=False)
    if result.returncode:
        # These commands contain no passwords or application settings.
        if result.stderr:
            print(result.stderr, file=sys.stderr)
        raise RuntimeError(f"{command[0]} {command[1]} failed ({result.returncode}).")
    return result.stdout.strip()


def fetch_health(url):
    request = Request(url, headers={"Cache-Control": "no-cache"})
    with urlopen(request, timeout=10) as response:
        return json.load(response)


def verify_release(base_url, expected, timeout=600, fetch=fetch_health,
                   sleep=time.sleep, clock=time.monotonic):
    deadline = clock() + timeout
    consecutive = 0
    last = "No response"
    while clock() < deadline:
        try:
            live = fetch(base_url + "/health/live")
            ready = fetch(base_url + "/health/ready")
            results = (live, ready)
            healthy = all(x.get("status") == "Healthy" for x in results)
            matching = expected is None or all(x.get("release") == expected for x in results)
            last = f"live={live.get('status')}, ready={ready.get('status')}, release={live.get('release')}"
            consecutive = consecutive + 1 if healthy and matching else 0
            if consecutive >= 2:
                return
        except (OSError, ValueError, TypeError, AttributeError) as error:
            consecutive = 0
            last = type(error).__name__
        print(f"Waiting for release: {last}", flush=True)
        sleep(10)
    raise RuntimeError(f"Release health check timed out: {last}")


def current_main(commit):
    repository = os.environ["GITHUB_REPOSITORY"]
    token = os.environ["GH_TOKEN"]
    request = Request(
        f"https://api.github.com/repos/{repository}/git/ref/heads/main",
        headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
                 "User-Agent": "SAW-CI-CD"},
    )
    with urlopen(request, timeout=30) as response:
        return json.load(response)["object"]["sha"] == commit


def read_image(directory, commit, run_id, attempt):
    directory = Path(directory)
    build = json.loads((directory / "build.json").read_text(encoding="utf-8-sig"))
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Invalid source commit.")
    if (build.get("commit") != commit or str(build.get("run_id")) != run_id
            or str(build.get("run_attempt")) != attempt):
        raise ValueError("Image artifact belongs to a different commit or workflow run.")
    if build.get("image_name") != f"saw-api:ci-{commit}":
        raise ValueError("Unexpected image name in artifact.")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", build.get("image_id", "")):
        raise ValueError("Invalid image identity.")
    if not re.fullmatch(r"\d{14}_[A-Za-z0-9_]+", build.get("required_migration", "")):
        raise ValueError("Invalid migration metadata.")
    digest = hashlib.sha256()
    with (directory / "image.tar.gz").open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    if digest.hexdigest() != build.get("archive_sha256"):
        raise ValueError("Tested image archive checksum does not match.")
    return build


def read_build(directory, commit, run_id, attempt, database_record="artifacts/migration-result.json"):
    build = read_image(directory, commit, run_id, attempt)
    record = json.loads(Path(database_record).read_text(encoding="utf-8-sig"))
    if (record.get("status") != "verified" or record.get("commit") != commit
            or str(record.get("run_id")) != run_id or str(record.get("run_attempt")) != attempt
            or record.get("required_migration") != build["required_migration"]
            or record.get("after") != build["required_migration"]):
        raise ValueError("Azure database has not been verified for this release and workflow attempt.")
    return build


class Deployment:
    def __init__(self, group, registry, app, commit, run_id, attempt,
                 directory="artifacts/image", record="artifacts/deployment.json",
                 runner=run, verifier=verify_release, head_check=current_main,
                 health_reader=fetch_health, database_record="artifacts/migration-result.json"):
        self.group, self.registry, self.app = group, registry, app
        self.commit, self.run_id, self.attempt = commit, str(run_id), str(attempt)
        self.directory, self.record_path = Path(directory), Path(record)
        self.runner, self.verifier = runner, verifier
        self.head_check, self.health_reader = head_check, health_reader
        self.database_record = database_record
        self.record = {"commit": commit, "run_id": str(run_id), "run_attempt": str(attempt),
                       "app": app, "status": "pending", "previous_image": None,
                       "new_image": None, "previous_release": None, "rollback": "not-needed"}

    def az(self, *arguments, output="json"):
        result = self.runner(["az", *arguments, "--only-show-errors", "--output", output])
        return json.loads(result) if output == "json" else result

    def container(self):
        result = self.az("webapp", "sitecontainers", "show", "--name", self.app,
                         "--resource-group", self.group, "--container-name", "main")
        # Azure CLI serializes a flattened model; tolerate ARM-shaped responses too.
        return result.get("properties", result)

    def update(self, image):
        self.az("webapp", "sitecontainers", "update", "--name", self.app,
                "--resource-group", self.group, "--container-name", "main", "--image", image,
                output="none")
        self.az("webapp", "restart", "--name", self.app, "--resource-group", self.group,
                output="none")

    def pinned_image(self, image, login_server):
        prefix = login_server + "/saw-api"
        if not image.startswith(prefix + ":") and not image.startswith(prefix + "@"):
            raise ValueError("Current container does not use saw-api in the selected registry.")
        if "@" in image:
            digest = image.rsplit("@", 1)[1]
        else:
            digest = self.az("acr", "repository", "show", "--name", self.registry,
                             "--image", image[len(login_server) + 1:], "--query", "digest",
                             output="tsv")
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
            raise ValueError("Cannot resolve a valid registry digest.")
        return prefix + "@" + digest

    def execute(self):
        attempted = False
        configured_before = None
        try:
            for name, value in (("resource group", self.group), ("registry", self.registry),
                                ("web app", self.app)):
                if not value or not value.strip():
                    raise ValueError(f"Missing GitHub variable for {name}.")
            if not self.run_id.isdigit() or not self.attempt.isdigit():
                raise ValueError("Invalid GitHub workflow run identity.")
            build = read_build(self.directory, self.commit, self.run_id, self.attempt, self.database_record)
            self.record["required_migration"] = build["required_migration"]
            if not self.head_check(self.commit):
                self.record["status"] = "superseded"
                print("A newer commit is already on main; deployment skipped.")
                return

            registry = self.az("acr", "show", "--name", self.registry,
                               "--resource-group", self.group)
            login_server = registry["loginServer"].lower()
            if not re.fullmatch(r"[a-z0-9-]+\.azurecr\.io", login_server):
                raise ValueError("Unexpected registry login server.")
            app = self.az("webapp", "show", "--name", self.app, "--resource-group", self.group,
                          "--query", "{host:defaultHostName}")
            host = app["host"]
            if not re.fullmatch(r"[a-z0-9.-]+\.azurewebsites\.net", host):
                raise ValueError("Unexpected App Service hostname.")
            base_url = "https://" + host
            before = self.container()
            if before.get("isMain") is not True or str(before.get("targetPort")) != "8080":
                raise ValueError("Expected sidecar main container on port 8080.")
            if before.get("authType") != "UserAssigned" or not before.get("userManagedIdentityClientId"):
                raise ValueError("Expected the existing user-assigned identity for image pulls.")
            configured_before = before["image"]
            previous = self.pinned_image(configured_before, login_server)
            self.record["previous_image"] = previous
            self.record["url"] = base_url
            try:
                self.record["previous_release"] = self.health_reader(base_url + "/health/live").get("release")
            except (OSError, ValueError, TypeError, AttributeError):
                pass

            self.az("acr", "login", "--name", self.registry, output="none")
            print("Loading the tested image artifact.", flush=True)
            self.runner(["docker", "image", "load", "--input", str(self.directory / "image.tar.gz")])
            actual_id = self.runner(["docker", "image", "inspect", "--format", "{{.Id}}", build["image_name"]])
            if actual_id != build["image_id"]:
                raise ValueError("Loaded image differs from the tested image identity.")
            tag = f"sha-{self.commit}-{self.run_id}-{self.attempt}"
            tagged = f"{login_server}/saw-api:{tag}"
            self.runner(["docker", "image", "tag", build["image_name"], tagged])
            print(f"Pushing {tagged}", flush=True)
            self.runner(["docker", "image", "push", tagged])
            image = self.pinned_image(tagged, login_server)
            self.record["new_image"] = image
            self.record["image_tag"] = tag
            if not self.head_check(self.commit):
                self.record["status"] = "superseded"
                print("Main changed while pushing; the app was not updated.")
                return

            # Mark before issuing the update: a failed request can have applied server-side.
            attempted = True
            print(f"Updating main container to {image}", flush=True)
            self.update(image)
            after = self.container()
            if after.get("image") != image:
                raise RuntimeError("App Service did not retain the requested image digest.")
            if (after.get("authType"), after.get("userManagedIdentityClientId")) != (
                    before.get("authType"), before.get("userManagedIdentityClientId")):
                raise RuntimeError("Image pull identity changed unexpectedly.")
            self.verifier(base_url, self.commit)
            self.record["status"] = "deployed"
            print(f"DEPLOYED: {image}; release={self.commit}", flush=True)
        except Exception:
            self.record["status"] = "failed"
            if attempted:
                try:
                    current = self.container()["image"]
                    if current not in (self.record["new_image"], configured_before, self.record["previous_image"]):
                        raise RuntimeError("App image changed outside this run; refusing to overwrite it.")
                    self.update(self.record["previous_image"])
                    self.verifier(self.record["url"], self.record["previous_release"])
                    self.record["rollback"] = "succeeded"
                    print("ROLLBACK: previous digest restored; this workflow still fails.", flush=True)
                except Exception as rollback_error:
                    self.record["rollback"] = "failed"
                    print(f"ROLLBACK NEEDS ATTENTION: {rollback_error}", file=sys.stderr)
            raise
        finally:
            self.record_path.parent.mkdir(parents=True, exist_ok=True)
            self.record_path.write_text(json.dumps(self.record, indent=2) + "\n", encoding="utf-8")
            summary = os.environ.get("GITHUB_STEP_SUMMARY")
            if summary:
                with open(summary, "a", encoding="utf-8") as stream:
                    stream.write(f"### Azure deployment\n\nStatus: `{self.record['status']}`\n\n")
                    stream.write(f"Commit: `{self.commit}`\n\n")
                    for field in ("new_image", "previous_image", "rollback"):
                        stream.write(f"{field}: `{self.record[field]}`\n\n")


def main():
    deployment = Deployment(
        os.environ.get("AZURE_RESOURCE_GROUP", ""), os.environ.get("AZURE_ACR_NAME", ""),
        os.environ.get("AZURE_WEBAPP_NAME", ""), os.environ["GITHUB_SHA"],
        os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"],
    )
    try:
        deployment.execute()
    except Exception as error:
        print(f"DEPLOYMENT FAILED: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
