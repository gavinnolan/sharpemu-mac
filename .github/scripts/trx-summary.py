#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Summarise dotnet test .trx files as Markdown for $GITHUB_STEP_SUMMARY.

Usage: trx-summary.py <results-dir>

Prints one table row per test assembly plus the names of failed tests. Always
exits 0: it reports results, the test step itself decides pass or fail.
"""

import pathlib
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
MAX_FAILURES = 50


def assembly_name(root: ET.Element, trx: pathlib.Path) -> str:
    for test_method in root.iterfind(".//t:UnitTest/t:TestMethod", NS):
        code_base = test_method.get("codeBase")
        if code_base:
            return pathlib.PurePath(code_base.replace("\\", "/")).name
    return trx.stem


def main() -> int:
    results_dir = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else pathlib.Path(".")
    trx_files = sorted(results_dir.rglob("*.trx"))

    print("## Test results (osx-x64)")
    print()
    if not trx_files:
        print("No .trx files were produced. The test run probably failed before executing tests.")
        return 0

    rows = []
    failures = []
    totals = {"total": 0, "passed": 0, "failed": 0, "skipped": 0}

    for trx in trx_files:
        try:
            root = ET.parse(trx).getroot()
        except ET.ParseError as error:
            rows.append((trx.name, "unreadable: %s" % error, "", "", ""))
            continue

        counters = root.find(".//t:ResultSummary/t:Counters", NS)
        if counters is None:
            rows.append((trx.name, "no counters", "", "", ""))
            continue

        total = int(counters.get("total", "0"))
        passed = int(counters.get("passed", "0"))
        failed = int(counters.get("failed", "0"))
        skipped = int(counters.get("notExecuted", "0"))
        totals["total"] += total
        totals["passed"] += passed
        totals["failed"] += failed
        totals["skipped"] += skipped
        name = assembly_name(root, trx)
        rows.append((name, str(total), str(passed), str(failed), str(skipped)))

        for result in root.iterfind(".//t:UnitTestResult", NS):
            if result.get("outcome") == "Failed":
                failures.append((name, result.get("testName", "?")))

    verdict = "FAILED" if totals["failed"] else "passed"
    print(
        "**%s**: %d total, %d passed, %d failed, %d skipped."
        % (verdict, totals["total"], totals["passed"], totals["failed"], totals["skipped"])
    )
    print()
    print("| Assembly | Total | Passed | Failed | Skipped |")
    print("| --- | ---: | ---: | ---: | ---: |")
    for name, total, passed, failed, skipped in rows:
        print("| %s | %s | %s | %s | %s |" % (name, total, passed, failed, skipped))

    if failures:
        print()
        print("### Failed tests")
        print()
        for assembly, name in failures[:MAX_FAILURES]:
            print("- `%s` (%s)" % (name, assembly))
        if len(failures) > MAX_FAILURES:
            print("- ...and %d more (see the test-results artifact)." % (len(failures) - MAX_FAILURES))

    return 0


if __name__ == "__main__":
    sys.exit(main())
