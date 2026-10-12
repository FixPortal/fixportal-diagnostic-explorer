"""Run the pinned bootstrap; fake network/build tools, verify its fail-closed graph check."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
BASH = str(Path(os.environ["ProgramFiles"]) / "Git/bin/bash.exe") if os.name == "nt" else shutil.which("bash")
PIN = "1ae2b3c591a322d209cfdcd50bd2f10de20b8336"


class BuildStrykerTests(unittest.TestCase):
    def run_bootstrap(self, version="10.0.12", fail="", head=PIN):
        with tempfile.TemporaryDirectory() as directory:
            temp = Path(directory)
            tools = temp / "tools"
            tools.mkdir()
            (tools / "git").write_text(
                '#!/usr/bin/env bash\nset -eu\nprintf "git %s\\n" "$*" >> "$CALLS"\n'
                '[[ "${FAIL:-}" != git ]] || exit 7\n'
                'if [[ "$*" == *rev-parse* ]]; then printf "%s\\n" "$PIN"; fi\n', newline="\n")
            (tools / "dotnet").write_text(
                '#!/usr/bin/env bash\nset -eu\nprintf "dotnet %s\\n" "$*" >> "$CALLS"\n'
                '[[ "${FAIL:-}" != "$1" ]] || exit 8\n'
                'if [[ "$1" == build ]]; then\n'
                '  while [[ "$1" != -o ]]; do shift; done\n'
                '  mkdir -p "$2"\n'
                '  printf \'{"libraries":{"System.Security.Cryptography.Xml/%s":{}}}\\n\' "$XML_VERSION" > "$2/Stryker.CLI.deps.json"\n'
                'fi\n', newline="\n")
            for tool in tools.iterdir():
                tool.chmod(0o755)
            calls = temp / "calls"
            env = dict(os.environ, TOOLS=tools.as_posix(), SCRIPT=(ROOT / "scripts/build-stryker.sh").as_posix(),
                       OUTPUT=(temp / "output").as_posix(), CALLS=calls.as_posix(), PIN=head,
                       XML_VERSION=version, FAIL=fail)
            result = subprocess.run([BASH, "-c", 'export PATH="$(cd "$TOOLS" && pwd):$PATH"; exec bash "$SCRIPT" "$OUTPUT"'],
                                    env=env, capture_output=True, text=True, timeout=30)
            return result, calls.read_text() if calls.exists() else ""

    def test_patched_graph_and_locked_source_build(self):
        result, calls = self.run_bootstrap()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("https://github.com/stryker-mutator/stryker-net.git", calls)
        self.assertIn("fetch --depth=1 origin " + PIN, calls)
        self.assertIn("checkout --detach " + PIN, calls)
        self.assertIn("--locked-mode --source https://api.nuget.org/v3/index.json", calls)
        self.assertIn("-c Release --no-restore", calls)

    def test_rejects_unpatched_or_missing_xml(self):
        for version in ("9.0.0", "10.0.10", "missing"):
            with self.subTest(version=version):
                result, _ = self.run_bootstrap(version)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Refusing unexpected Stryker XML dependency", result.stderr)

    def test_checkout_must_match_the_pinned_revision(self):
        result, calls = self.run_bootstrap(head="0" * 40)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("dotnet restore", calls)

    def test_native_failures_stop_the_bootstrap(self):
        for fail in ("git", "restore", "build"):
            with self.subTest(fail=fail):
                result, calls = self.run_bootstrap(fail=fail)
                self.assertNotEqual(result.returncode, 0)
                if fail != "build":
                    self.assertNotIn("dotnet build", calls)

    def test_workflow_uses_patched_dll_and_manifest_drops_published_bundle(self):
        workflow = (ROOT / ".github/workflows/mutation.yml").read_text()
        self.assertIn('bash scripts/build-stryker.sh "$RUNNER_TEMP/stryker"', workflow)
        self.assertIn('dotnet "$RUNNER_TEMP/stryker/bin/Stryker.CLI.dll" --config-file stryker-config.json', workflow)
        tools = json.loads((ROOT / ".config/dotnet-tools.json").read_text())["tools"]
        self.assertNotIn("dotnet-stryker", tools)


if __name__ == "__main__":
    unittest.main()
