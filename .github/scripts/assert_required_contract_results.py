"""Require executed Mongo and offline Log Analytics contracts in the backend TRX."""

from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def verify(roots: list[ET.Element]) -> dict[str, int]:
    results = [node for root in roots for node in root.iter() if node.tag.rsplit("}", 1)[-1] == "UnitTestResult"]
    required = {
        "MongoRetroLoggerIntegrationTests.": None,
        "LogAnalyticsWireContractTests.": 2,
    }
    counts = {}
    for name, expected in required.items():
        matching = [node for node in results if name in node.get("testName", "")]
        if not matching or (expected is not None and len(matching) != expected):
            raise ValueError(f"{name}: missing required executed cases; found {len(matching)}")
        if any(node.get("outcome") != "Passed" for node in matching):
            raise ValueError(f"{name}: required contract failed or was skipped")
        if name == "LogAnalyticsWireContractTests.":
            for count in (1, 251):
                if not any(node.get("testName", "").endswith(f"(count: {count})") for node in matching):
                    raise ValueError(f"{name}: missing SDK batch scenario count={count}")
        counts[name] = len(matching)
    return counts


if __name__ == "__main__":
    files = sorted(Path(sys.argv[1]).rglob("*.trx"))
    try:
        counts = verify([ET.parse(file).getroot() for file in files])
    except (ValueError, ET.ParseError) as error:
        raise SystemExit(str(error)) from error
    print(f"Required persistence contracts executed and passed: {counts}")
