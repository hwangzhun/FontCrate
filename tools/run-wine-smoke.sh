#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")/.." && pwd)"
smoke_dir="$project_dir/artifacts/wine-smoke"
export WINEPREFIX="$smoke_dir/prefix"
export WINEDLLOVERRIDES='mshtml=;zlib1=n'
exec "$smoke_dir/runtime/usr/lib/x86_64-linux-gnu/wine/wine" "$project_dir/artifacts/FontCrate-win-x64/FontCrate.exe" "$@"
