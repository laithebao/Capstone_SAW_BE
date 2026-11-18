"""Open only this runner's IPv4; close only the matching rule owned by this run."""

import ipaddress
import json
import os
from pathlib import Path
import re
import sys
from urllib.request import urlopen

from deploy_azure import run


RECORD = Path("artifacts/sql-firewall.json")


def settings():
    group, server = os.environ["AZURE_RESOURCE_GROUP"], os.environ["AZURE_SQL_SERVER"]
    run_id, attempt = os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"]
    if not group.strip() or not re.fullmatch(r"[a-z0-9-]+", server):
        raise ValueError("Invalid SQL firewall target.")
    if not run_id.isdigit() or not attempt.isdigit():
        raise ValueError("Invalid workflow identity.")
    return group, server, f"gha-saw-{run_id}-{attempt}"


def az(group, server, *args):
    return run(["az", "sql", "server", "firewall-rule", *args,
                "--resource-group", group, "--server", server, "--only-show-errors"])


def public_ipv4(value):
    address = ipaddress.ip_address(value.strip())
    if address.version != 4 or not address.is_global:
        raise ValueError("Expected the runner's public IPv4 address.")
    return str(address)


def open_rule():
    group, server, name = settings()
    existing = json.loads(az(group, server, "list", "--output", "json"))
    if any(rule["name"] == name for rule in existing):
        raise ValueError("Firewall rule already exists; refusing to overwrite it.")
    with urlopen("https://api.ipify.org", timeout=30) as response:
        address = public_ipv4(response.read(64).decode("ascii"))
    RECORD.parent.mkdir(parents=True, exist_ok=True)
    # Persist BEFORE create, so cleanup can handle a lost response after server-side success.
    RECORD.write_text(json.dumps({"group": group, "server": server, "name": name, "ip": address}), encoding="utf-8")
    az(group, server, "create", "--name", name, "--start-ip-address", address,
       "--end-ip-address", address, "--output", "none")
    print("Temporary SQL access created for this runner only.")


def close_rule():
    if not RECORD.exists():
        return
    group, server, name = settings()
    record = json.loads(RECORD.read_text(encoding="utf-8"))
    if (record["group"], record["server"], record["name"]) != (group, server, name):
        raise ValueError("Firewall record belongs to another target or workflow.")
    rules = json.loads(az(group, server, "list", "--output", "json"))
    match = next((rule for rule in rules if rule["name"] == name), None)
    if match is not None:
        if match["startIpAddress"] != record["ip"] or match["endIpAddress"] != record["ip"]:
            raise ValueError("Firewall rule changed externally; refusing to delete it.")
        az(group, server, "delete", "--name", name, "--output", "none")
    print("Temporary SQL firewall rule removed or already absent.")


if __name__ == "__main__":
    if sys.argv[1:] == ["open"]:
        open_rule()
    elif sys.argv[1:] == ["close"]:
        close_rule()
    else:
        raise SystemExit("Usage: sql_firewall.py (open|close)")
