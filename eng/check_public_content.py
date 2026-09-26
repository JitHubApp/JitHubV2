"""Catch private operational metadata before it lands in public GitHub content."""

from __future__ import annotations

import json
import os
import pathlib
import re
import subprocess
import sys


CHECKS = {
    "UUID-like identifier": re.compile(
        r"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        re.IGNORECASE,
    ),
    "local Windows user path": re.compile(r"\b[A-Za-z]:(?:\\|/)Users(?:\\|/)[^\s\\/]+"),
    "support case number": re.compile(
        r"\b(?:support|customer)\s+(?:case|ticket|request)\s*(?:#|number|id|:)?\s*\d{5,}\b",
        re.IGNORECASE,
    ),
}


def main() -> int:
    root = pathlib.Path(__file__).resolve().parents[1]
    tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode("utf-8")
    findings = []

    def check_text(source: str, value: str) -> None:
        for number, line in enumerate(value.splitlines(), 1):
            for description, pattern in CHECKS.items():
                if pattern.search(line):
                    findings.append(f"{source}:{number}: {description}")

    for name in filter(None, tracked.split("\0")):
        if not name.lower().endswith(".md"):
            continue
        check_text(name, (root / name).read_text(encoding="utf-8-sig"))

    event_path = os.environ.get("GITHUB_EVENT_PATH")
    if event_path:
        event = json.loads(pathlib.Path(event_path).read_text(encoding="utf-8"))
        pull_request = event.get("pull_request")
        if pull_request:
            check_text("PR title", pull_request.get("title") or "")
            check_text("PR description", pull_request.get("body") or "")

    base_sha = os.environ.get("BASE_SHA")
    if base_sha:
        messages = subprocess.check_output(
            ["git", "log", "--format=%B%x00", f"{base_sha}..HEAD"], cwd=root
        ).decode("utf-8", errors="replace")
        for index, message in enumerate(messages.split("\0"), 1):
            check_text(f"commit message {index}", message)

    if findings:
        print("Review public content for private operational metadata:")
        print("\n".join(findings))
        return 1

    print("No private operational metadata patterns found in Markdown or PR metadata.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
