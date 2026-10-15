"""Require executed Mongo and offline Log Analytics contracts in the backend TRX."""

from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def verify(roots: list[ET.Element]) -> dict[str, int]:
    results = [node for root in roots for node in root.iter() if node.tag.rsplit("}", 1)[-1] == "UnitTestResult"]
    mongo_methods = {
        "WriteMessages_DuplicatePlusRejectedDocument_Propagates",
        "WriteMessages_DuplicateKeyOnlyBatch_IsTolerated",
        "WriteMessages_StoresTheEnvironment",
        "Delete_RemovesOnlyNamedRecords_SkipsMalformedIds_AndReportsCount",
        "GetMessages_FilterBoundaries_AreInclusiveStartAndMinLevel_ExclusiveEnd",
        "WriteMessages_InsertsDocument_WithAllFieldsIntact",
        "WriteMessages_WithCancelledToken_ThrowsOperationCanceled",
        "WriteMessages_UsesTheConfiguredDatabaseAndCollection",
        "GetMessages_MoreRowsThanOneBatch_AreAllEnumeratedAcrossBatches",
        "WriteMessages_NonDuplicateWriteFailure_Propagates",
        "WriteMessages_DuplicateFirstInABatch_StillStoresTheValidDocumentBehindIt",
        "GetMessages_FiltersOrdersAndLimits",
    }
    required = {
        "MongoRetroLoggerIntegrationTests.": mongo_methods,
        "LogAnalyticsWireContractTests.": {
            f"Upload_and_query_preserve_every_schema_field_through_the_real_sdk(count: {count})"
            for count in (1, 251)
        },
    }
    counts = {}
    for name, expected in required.items():
        prefix = f"DiagnosticService.UnitTests.{name}"
        matching = [node for node in results if node.get("testName", "").startswith(prefix)]
        identities = [node.get("testName")[len(prefix):] for node in matching]
        if not expected.issubset(identities):
            raise ValueError(f"{name}: missing required executed cases; found {len(matching)}")
        if len(identities) != len(set(identities)):
            raise ValueError(f"{name}: duplicate case results")
        if any(node.get("outcome") != "Passed" for node in matching):
            raise ValueError(f"{name}: required contract failed or was skipped")
        counts[name] = len(matching)
    return counts


if __name__ == "__main__":
    files = sorted(Path(sys.argv[1]).rglob("*.trx"))
    try:
        counts = verify([ET.parse(file).getroot() for file in files])
    except (ValueError, ET.ParseError) as error:
        raise SystemExit(str(error)) from error
    print(f"Required persistence contracts executed and passed: {counts}")
