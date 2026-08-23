#!/usr/bin/env python3
"""Regression gate: the shipped mod version must be consistent across sources.

Checks that:
1. Source/EfficientServer/ModInfo.xml and dist/EfficientServer/ModInfo.xml
   carry the same version.
2. AssemblyInfo.cs AssemblyVersion matches ModInfo (ModInfo "1.17.0" ==
   Assembly "1.17.0.0", trailing ".0" parts ignored).
3. docs/ claim no version newer than the shipped one (catches the v1.18
   drift class where docs referenced a release that never shipped).
4. CHANGELOG.md exists and mentions the shipped mod version, so a release
   cannot tag without its changelog entry.

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
CHANGELOG = ROOT / "CHANGELOG.md"
DOCS = ROOT / "docs"

USAGE = """\
usage: check_version.py [-h | --help]

Gate: ModInfo.xml, AssemblyInfo.cs and the dist ModInfo must carry consistent
versions, docs must not claim a version newer than shipped, and CHANGELOG.md
must mention the shipped version. Wired into `make test`. Takes no options
besides -h/--help.\
"""


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

    if mi and (CHANGELOG.exists() is False or mi not in CHANGELOG.read_text(encoding="utf-8")):
        fails.append(f"CHANGELOG.md missing or has no entry for shipped mod version {mi}")

    if fails:
        print("FAIL:", file=sys.stderr)
        for f in fails:
            print(f"  {f}", file=sys.stderr)
        return 1
    print(f"OK: versions consistent (ModInfo {mi}, Assembly {asm}, dist {di})")
    return 0


if __name__ == "__main__":
    argv = sys.argv[1:]
    if argv in (["-h"], ["--help"]):
        print(USAGE)
        raise SystemExit(0)
    if argv:
        print(f"check_version.py: unrecognized arguments: {' '.join(argv)}", file=sys.stderr)
        print(USAGE, file=sys.stderr)
        raise SystemExit(2)
    sys.exit(main())
