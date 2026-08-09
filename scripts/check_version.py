#!/usr/bin/env python3
"""Regression gate: the shipped mod version must be consistent across sources.

Checks that:
1. Source/EfficientServer/ModInfo.xml and dist/EfficientServer/ModInfo.xml
   carry the same version.
2. AssemblyInfo.cs AssemblyVersion matches ModInfo (ModInfo "1.17.0" ==
   Assembly "1.17.0.0", trailing ".0" parts ignored).
3. docs/ claim no version newer than the shipped one (catches the v1.18
   drift class where docs referenced a release that never shipped).

Run: python3 scripts/check_version.py   (wired into `make test`)
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MODINFO = ROOT / "Source" / "EfficientServer" / "ModInfo.xml"
DIST_MODINFO = ROOT / "dist" / "EfficientServer" / "ModInfo.xml"
ASSEMBLY = ROOT / "Source" / "EfficientServer" / "AssemblyInfo.cs"
DOCS = ROOT / "docs"


def modinfo_version(path: Path) -> str | None:
    m = re.search(r'Version\s+value="([0-9.]+)"', path.read_text(encoding="utf-8"))
    return m.group(1) if m else None


def norm(v: str) -> tuple[int, ...]:
    return tuple(int(x) for x in v.split("."))


def main() -> int:
    fails = []

    mi = modinfo_version(MODINFO)
    if mi is None:
        fails.append("ModInfo.xml: no Version value")
    di = modinfo_version(DIST_MODINFO) if DIST_MODINFO.exists() else None

    asm_m = re.search(r'AssemblyVersion\("([0-9.]+)"\)', ASSEMBLY.read_text(encoding="utf-8"))
    asm = asm_m.group(1) if asm_m else None

    if mi and asm:
        # trailing ".0" parts in the 4-part assembly version are cosmetic
        if norm(mi) != norm(asm)[: len(norm(mi))]:
            fails.append(f"ModInfo {mi} != AssemblyInfo {asm}")
    if di and mi and norm(di) != norm(mi):
        fails.append(f"dist ModInfo {di} != source ModInfo {mi}")

    if mi:
        shipped = norm(mi)
        # docs should not claim a future minor (v1.18 drift class); skip
        # changelog sections, which legitimately describe version history.
        for f in sorted(DOCS.glob("*.md")):
            txt = f.read_text(encoding="utf-8", errors="replace")
            body = txt.split("## Changelog", 1)[0]
            for m in re.finditer(r"v1\.(\d+)", body):
                minor = int(m.group(1))
                if minor > shipped[1]:
                    fails.append(f"{f.name}: claims v1.{minor} > shipped {mi}")

    if fails:
        print("FAIL:")
        for f in fails:
            print(f"  {f}")
        return 1
    print(f"OK: versions consistent (ModInfo {mi}, Assembly {asm}, dist {di})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
