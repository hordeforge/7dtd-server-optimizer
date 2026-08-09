#!/usr/bin/env python3
"""Regression gate: every ServerPerfConfig field must be documented in CONFIG.md.

Cross-checks the property names declared in Source/EfficientServer/Config.cs
against the backticked identifiers used in docs/CONFIG.md. A field is "covered"
when its bare name appears in a doc token (e.g. `ResolveEveryNTicks` or
`CrowdCollisionLod.ResolveEveryNTicks`). Fails with the missing names so a new
config option cannot ship undocumented.

Run: python3 scripts/check_config_doc.py   (wired into `make test`)
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONFIG_CS = ROOT / "Source" / "EfficientServer" / "Config.cs"
CONFIG_MD = ROOT / "docs" / "CONFIG.md"

# Names that are methods, not config fields (skip).
NON_FIELDS = {"FeatureActive", "ShouldRunFor", "Normalize", "Load", "DefaultPathBesideAssembly"}


def main() -> int:
    src = CONFIG_CS.read_text(encoding="utf-8")
    doc = CONFIG_MD.read_text(encoding="utf-8")

    fields = sorted(
        set(re.findall(r"public (?:bool|int|float|string|double|long) ([A-Za-z]+)", src))
        - NON_FIELDS
    )
    doc_tokens = set(re.findall(r"`([A-Za-z]+(?:\.[A-Za-z]+)?)`", doc))
    doc_names = {t.split(".")[-1] for t in doc_tokens}

    missing = [f for f in fields if f not in doc_names]
    if missing:
        print(f"FAIL: {len(missing)} config field(s) undocumented in {CONFIG_MD.name}:")
        for m in missing:
            print(f"  {m}")
        return 1

    print(f"OK: all {len(fields)} config fields documented in {CONFIG_MD.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
