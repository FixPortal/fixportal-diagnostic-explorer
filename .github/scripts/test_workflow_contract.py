"""Repository-specific wiring alongside the canonical gate semantics checker."""
from pathlib import Path
import json
import re
import unittest

from assert_gate_coverage import job_block, job_needs, read_gate_contract

ROOT = Path(__file__).resolve().parents[2]


class WorkflowContractTests(unittest.TestCase):
    def setUp(self):
        self.lines = (ROOT / ".github/workflows/ci.yml").read_text().splitlines(True)
        self.jobs, self.needs, _ = read_gate_contract(self.lines, "ci-gate")
        header = self.lines[self.jobs["ci-gate"]]
        self.indent = len(header) - len(header.lstrip(" "))

    def test_formatter_precedes_package_restore(self):
        commands = "\n".join(job_block(self.lines, self.jobs, "backend"))
        self.assertLess(commands.index("dotnet tool restore"), commands.index("dotnet csharpier check ."))
        self.assertLess(commands.index("dotnet csharpier check ."), commands.index("dotnet restore DiagnosticExplorer.slnx"))

    def test_frontend_validation_blocks_the_gate(self):
        self.assertIn("frontend", self.needs)
        commands = "\n".join(job_block(self.lines, self.jobs, "frontend"))
        for command in ("npm ci", "npm run lint", "npm test", "npm run build"):
            with self.subTest(command=command):
                self.assertIn(command, commands)

    def test_publication_waits_for_the_gate(self):
        self.assertIn("build-and-push", self.jobs)
        self.assertEqual(job_needs(self.lines, self.jobs, "build-and-push", self.indent), {"ci-gate"})
        publish = job_block(self.lines, self.jobs, "build-and-push")
        self.assertFalse(any(line.startswith("    if:") for line in publish))
        self.assertFalse((ROOT / ".github/workflows/publish-docker-image.yml").exists())
        self.assertIn("  cancel-in-progress: ${{ !startsWith(github.ref, 'refs/heads/main') && !startsWith(github.ref, 'refs/tags/') }}\n", self.lines)

    def test_web_mutation_partitions_preserve_the_configured_scope(self):
        workflow = (ROOT / ".github/workflows/mutation-web.yml").read_text()
        self.assertIn("      matrix:", workflow, "the serial web workload exceeds its execution budget")
        config = json.loads((ROOT / "diagnostics-web/stryker.conf.json").read_text())
        web = ROOT / "diagnostics-web"

        def expand(patterns):
            included = set()
            excluded = set()
            for pattern in patterns:
                (excluded if pattern.startswith("!") else included).update(web.glob(pattern.lstrip("!")))
            return included - excluded

        expected = expand(config["mutate"])
        actual = set()
        partitions = re.findall(r"^\s+mutate: (.+)$", workflow, re.MULTILINE)
        self.assertTrue(partitions)
        for partition in partitions:
            files = expand(partition.split(","))
            self.assertTrue(files, partition)
            self.assertFalse(actual & files, partition)
            actual |= files
        self.assertEqual(expected, actual)
        timeouts = re.findall(r"timeout-minutes: (\d+)", workflow)
        self.assertTrue(timeouts)
        for timeout in timeouts:
            self.assertLessEqual(int(timeout), 30)

    def test_dotnet_mutation_uses_covering_tests_within_the_execution_budget(self):
        workflow = (ROOT / ".github/workflows/mutation.yml").read_text()
        timeouts = re.findall(r"timeout-minutes: (\d+)", workflow)
        self.assertTrue(timeouts)
        for timeout in timeouts:
            self.assertLessEqual(int(timeout), 30)
        config = json.loads((ROOT / "tests/DiagnosticService.UnitTests/stryker-config.json").read_text())["stryker-config"]
        self.assertEqual(config["coverage-analysis"], "perTest")
        self.assertEqual(config["test-runner"], "mtp")
        tools = json.loads((ROOT / ".config/dotnet-tools.json").read_text())["tools"]
        self.assertNotIn("dotnet-stryker", tools)
        self.assertIn('bash scripts/build-stryker.sh "$RUNNER_TEMP/stryker"', workflow)
        self.assertEqual(config["mutate"], ["**/*.cs"])


if __name__ == "__main__":
    unittest.main()
