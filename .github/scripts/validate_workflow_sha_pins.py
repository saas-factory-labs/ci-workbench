#!/usr/bin/env python3
"""Fail if any `uses:` reference in .github/workflows is not pinned to an immutable 40-character commit SHA.

A mutable tag such as `actions/checkout@v4` can be re-pointed at malicious code (supply-chain attack); a commit SHA cannot.
Local reusable workflows (./...) are always allowed; docker:// references must be pinned by @sha256: digest.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"
USES = re.compile(r"^\s*(?:-\s+)?uses:\s+(\S+)")   # also matches list items ("- uses: ...")
SHA40 = re.compile(r"^[0-9a-f]{40}$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")


def main() -> int:
    files = sorted([*WORKFLOWS.glob("*.yml"), *WORKFLOWS.glob("*.yaml")])
    if not files:
        print("No workflow files found.")
        return 0

    violations = []
    for file in files:
        for number, line in enumerate(file.read_text(encoding="utf-8").splitlines(), start=1):
            if line.lstrip().startswith("#"):
                continue
            match = USES.match(line)
            if not match:
                continue
            ref = match.group(1).strip("\"'")
            if ref.startswith("./"):
                continue
            if "@" not in ref:
                violations.append((file.name, number, ref, "no pin: missing '@<sha>'"))
                continue
            pin = ref.rsplit("@", 1)[1]
            if ref.startswith("docker://"):
                if not DIGEST.match(pin):
                    violations.append((file.name, number, ref, "docker reference is not pinned to a sha256 digest"))
            elif not SHA40.match(pin):
                violations.append((file.name, number, ref, f"'@{pin}' is not a 40-character commit SHA"))

    if not violations:
        print("PASS: all GitHub Actions workflow references are pinned to immutable SHAs.")
        return 0

    print(f"FAIL: {len(violations)} unpinned GitHub Actions reference(s):\n")
    for name, number, ref, reason in violations:
        print(f"  {name}:{number}: {ref}\n      {reason}")
    print("\nPin to the commit SHA and keep the tag as a trailing comment, e.g.  uses: actions/checkout@<40-hex>  # v4.3.1")
    return 1


if __name__ == "__main__":
    sys.exit(main())