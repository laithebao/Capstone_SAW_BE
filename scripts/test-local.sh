#!/usr/bin/env bash
set -euo pipefail
backend_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec dotnet run --project "$backend_dir/tools/SAW.Testing" -- test --profile all "$@"
