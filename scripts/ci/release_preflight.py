"""Check artifact pairing and skip obsolete releases before Azure access."""

import json
import os
from pathlib import Path

from deploy_azure import current_main, read_image


def validate_pair(build, plan):
    for image_key, plan_key in (("commit", "Commit"), ("run_id", "RunId"), ("run_attempt", "RunAttempt")):
        if str(build.get(image_key)) != str(plan.get(plan_key)):
            raise ValueError("Image and migration artifacts belong to different releases.")
    if not plan.get("Migrations") or build["required_migration"] != plan["Migrations"][-1]:
        raise ValueError("Image and migration artifact heads do not match.")


def main():
    build = read_image("artifacts/image", os.environ["GITHUB_SHA"],
                       os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"])
    plan = json.loads(Path("artifacts/migrations/plan.json").read_text(encoding="utf-8"))
    validate_pair(build, plan)
    current = current_main(os.environ["GITHUB_SHA"])
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        stream.write(f"current={'true' if current else 'false'}\n")
    if not current:
        print("A newer commit is on main. This release will not access Azure.")


if __name__ == "__main__":
    main()
