import unittest
import xml.etree.ElementTree as ET

from assert_required_contract_results import verify


class RequiredContractResultsTests(unittest.TestCase):
    @staticmethod
    def report():
        return ET.fromstring("""<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
          <UnitTestResult testName="DiagnosticService.UnitTests.MongoRetroLoggerIntegrationTests.Write" outcome="Passed"/>
          <UnitTestResult testName="DiagnosticService.UnitTests.LogAnalyticsWireContractTests.Upload(count: 1)" outcome="Passed"/>
          <UnitTestResult testName="DiagnosticService.UnitTests.LogAnalyticsWireContractTests.Upload(count: 251)" outcome="Passed"/>
          <UnitTestResult testName="Optional.LiveAzure" outcome="NotExecuted"/>
        </Results></TestRun>""")

    def test_real_contracts_must_pass_while_optional_live_lane_stays_explicit(self):
        self.assertEqual(verify([self.report()])["LogAnalyticsWireContractTests."], 2)

    def test_skipping_either_persistence_class_fails(self):
        for index in (0, 1):
            with self.subTest(index=index):
                root = self.report()
                list(root)[0][index].set("outcome", "NotExecuted")
                with self.assertRaisesRegex(ValueError, "failed or was skipped"):
                    verify([root])

    def test_missing_batch_case_fails(self):
        root = self.report()
        results = list(root)[0]
        results.remove(results[2])
        with self.assertRaisesRegex(ValueError, "missing required executed cases"):
            verify([root])

    def test_missing_mongo_cases_fails(self):
        root = self.report()
        results = list(root)[0]
        results.remove(results[0])
        with self.assertRaisesRegex(ValueError, "missing required executed cases"):
            verify([root])

    def test_missing_reports_fail(self):
        with self.assertRaisesRegex(ValueError, "missing required executed cases"):
            verify([])


if __name__ == "__main__":
    unittest.main()
