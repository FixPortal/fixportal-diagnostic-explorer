import unittest
from pathlib import Path
import xml.etree.ElementTree as ET

from assert_required_contract_results import verify


class RequiredContractResultsTests(unittest.TestCase):
    @staticmethod
    def report():
        return ET.parse(Path(__file__).parent / "fixtures/persistence-contract-results.trx").getroot()

    def test_real_contracts_must_pass_while_optional_live_lane_stays_explicit(self):
        self.assertEqual(verify([self.report()])["LogAnalyticsWireContractTests."], 2)

    def test_skipping_either_persistence_class_fails(self):
        for index in (0, 12):
            with self.subTest(index=index):
                root = self.report()
                list(root)[0][index].set("outcome", "NotExecuted")
                with self.assertRaisesRegex(ValueError, "failed or was skipped"):
                    verify([root])

    def test_missing_batch_case_fails(self):
        root = self.report()
        results = list(root)[0]
        results.remove(results[13])
        with self.assertRaisesRegex(ValueError, "missing required executed cases"):
            verify([root])

    def test_missing_mongo_cases_fails(self):
        root = self.report()
        results = list(root)[0]
        for node in list(results)[:12]:
            results.remove(node)
        with self.assertRaisesRegex(ValueError, "missing required executed cases"):
            verify([root])

    def test_missing_reports_fail(self):
        with self.assertRaisesRegex(ValueError, "missing required executed cases"):
            verify([])

    def test_partial_mongo_suite_fails(self):
        root = self.report()
        results = list(root)[0]
        results.remove(results[0])
        with self.assertRaisesRegex(ValueError, "missing required executed cases"):
            verify([root])

    def test_duplicate_results_fail(self):
        root = self.report()
        results = list(root)[0]
        results.append(ET.fromstring(ET.tostring(results[0])))
        with self.assertRaisesRegex(ValueError, "duplicate case results"):
            verify([root])

    def test_other_sdk_methods_cannot_stand_in_for_the_wire_theory(self):
        root = self.report()
        results = list(root)[0]
        for node in list(results)[12:14]:
            node.set("testName", node.get("testName").replace("Upload_and_query_preserve_every_schema_field_through_the_real_sdk", "WrongMethod"))
        with self.assertRaisesRegex(ValueError, "missing required executed cases"):
            verify([root])


if __name__ == "__main__":
    unittest.main()
