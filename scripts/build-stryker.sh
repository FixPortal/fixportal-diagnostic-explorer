#!/usr/bin/env bash
set -euo pipefail

# Published 5.0.0 bundles vulnerable XML 9.0.0; pin the upstream security fix until a patched release.
revision=1ae2b3c591a322d209cfdcd50bd2f10de20b8336
output=${1:?Pass a new output directory}
mkdir "$output"
git init -q "$output/source"
git -C "$output/source" remote add origin https://github.com/stryker-mutator/stryker-net.git
git -C "$output/source" fetch --depth=1 origin "$revision"
git -C "$output/source" checkout --detach "$revision"
test "$(git -C "$output/source" rev-parse HEAD)" = "$revision"

project="$output/source/src/Stryker.CLI/Stryker.CLI/Stryker.CLI.csproj"
dotnet restore "$project" --locked-mode --source https://api.nuget.org/v3/index.json
dotnet build "$project" -c Release --no-restore -o "$output/bin"
python3 - "$output/bin/Stryker.CLI.deps.json" <<'PY'
import json
import sys

libraries = json.load(open(sys.argv[1], encoding="utf-8"))["libraries"]
xml = [name for name in libraries if name.startswith("System.Security.Cryptography.Xml/")]
if xml != ["System.Security.Cryptography.Xml/10.0.12"]:
    raise SystemExit(f"Refusing unexpected Stryker XML dependency: {xml}")
print("Verified patched Stryker XML dependency: 10.0.12")
PY
