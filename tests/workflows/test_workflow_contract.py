"""Repository-specific wiring alongside the canonical gate semantics checker."""
from pathlib import Path
import json
import unittest

import yaml


ROOT = Path(__file__).resolve().parents[2]


class WorkflowContractTests(unittest.TestCase):
    def setUp(self):
        self.ci = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())

    def test_formatter_precedes_package_restore(self):
        commands = "\n".join(step.get("run", "") for step in self.ci["jobs"]["backend"]["steps"])
        self.assertLess(commands.index("dotnet tool restore"), commands.index("dotnet csharpier check ."))
        self.assertLess(commands.index("dotnet csharpier check ."), commands.index("dotnet restore DiagnosticExplorer.slnx"))

    def test_frontend_validation_blocks_the_gate(self):
        jobs = self.ci["jobs"]
        self.assertIn("frontend", jobs["ci-gate"]["needs"])
        commands = "\n".join(step.get("run", "") for step in jobs["frontend"]["steps"])
        for command in ("npm ci", "npm run lint", "npm test", "npm run build"):
            with self.subTest(command=command):
                self.assertIn(command, commands)

    def test_publication_waits_for_the_gate(self):
        self.assertIn("build-and-push", self.ci["jobs"])
        publish = self.ci["jobs"]["build-and-push"]
        self.assertEqual(publish["needs"], ["ci-gate"])
        self.assertNotIn("if", publish)
        self.assertFalse((ROOT / ".github/workflows/publish-docker-image.yml").exists())
        self.assertFalse(self.ci["concurrency"]["cancel-in-progress"])

    def test_web_mutation_partitions_preserve_the_configured_scope(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/mutation-web.yml").read_text())
        job = workflow["jobs"]["stryker-web"]
        self.assertIn("strategy", job, "the serial web workload exceeds its execution budget")
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
        for partition in job["strategy"]["matrix"]["include"]:
            files = expand(partition["mutate"].split(","))
            self.assertTrue(files, partition["name"])
            self.assertFalse(actual & files, partition["name"])
            actual |= files
        self.assertEqual(expected, actual)
        self.assertLessEqual(job["timeout-minutes"], 30)

    def test_dotnet_mutation_uses_covering_tests_within_the_execution_budget(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/mutation.yml").read_text())
        self.assertLessEqual(workflow["jobs"]["stryker"]["timeout-minutes"], 30)
        config = json.loads((ROOT / "tests/DiagnosticService.UnitTests/stryker-config.json").read_text())["stryker-config"]
        self.assertEqual(config["coverage-analysis"], "perTest")
        self.assertEqual(config["test-runner"], "mtp")
        tools = json.loads((ROOT / ".config/dotnet-tools.json").read_text())["tools"]
        self.assertGreaterEqual(int(tools["dotnet-stryker"]["version"].split(".")[0]), 5)
        self.assertEqual(config["mutate"], ["**/*.cs"])


if __name__ == "__main__":
    unittest.main()
