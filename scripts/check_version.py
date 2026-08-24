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

Run: python3 scripts/check_version.py
     python3 scripts/check_version.py --selftest     (both wired into `make test`)
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
usage: check_version.py [--selftest] [-h | --help]

Gate: ModInfo.xml, AssemblyInfo.cs and the dist ModInfo must carry consistent
versions, docs must not claim a version newer than shipped, and CHANGELOG.md
must mention the shipped version. Wired into `make test`.
  --selftest  exercise the version extraction/normalization logic itself (the
              repo gate above only fails on tree drift; it stays green if this
              script's own matching logic silently breaks)
  -h, --help  show this help\
"""


def modinfo_version(path: Path) -> str | None:
    m = re.search(r'Version\s+value="([0-9.]+)"', path.read_text(encoding="utf-8"))
    return m.group(1) if m else None


def norm(v: str) -> tuple[int, ...]:
    return tuple(int(x) for x in v.split("."))


def _selftest() -> int:
    """Pin the extraction/normalization primitives the consistency checks use.

    main() reads fixed repo paths, so the selftest drives its pure helpers on
    synthetic inputs: a synthetic ModInfo.xml for modinfo_version and literal
    strings for norm(), including the trailing-'.0' equivalence rule the
    ModInfo-vs-AssemblyVersion comparison depends on.
    """
    import tempfile

    failures: list[str] = []

    def check(name: str, cond: bool) -> None:
        if cond:
            print("PASS: " + name)
        else:
            print("FAIL: " + name, file=sys.stderr)
            failures.append(name)

    with tempfile.TemporaryDirectory(prefix="es-version-test.") as td:
        mi = Path(td) / "ModInfo.xml"
        # Same attribute layout as the real ModInfo.xml (tab-indented, value=).
        mi.write_text(
            '<?xml version="1.0" encoding="UTF-8" ?>\n<xml>\n'
            '\t<Name value="EfficientServer" />\n'
            '\t<Version value="1.17.0" />\n'
            "</xml>\n",
            encoding="utf-8",
        )
        check("modinfo_version reads the Version attribute", modinfo_version(mi) == "1.17.0")
        no_version = Path(td) / "NoVersion.xml"
        no_version.write_text('<xml>\n\t<Name value="X" />\n</xml>\n', encoding="utf-8")
        check(
            "modinfo_version returns None when Version is missing",
            modinfo_version(no_version) is None,
        )

    check("norm splits numeric parts", norm("1.17.0") == (1, 17, 0))
    # The shipped pair: ModInfo "1.17.0" vs AssemblyVersion "1.17.0.0". The
    # comparison in main() truncates the assembly tuple to the ModInfo length;
    # pin both the equal and the drifted outcome of exactly that expression.
    mi_v, asm_v = "1.17.0", "1.17.0.0"
    check("norm treats trailing .0 as cosmetic", norm(mi_v) == norm(asm_v)[: len(norm(mi_v))])
    other_mi, other_asm = "1.18.0", "1.17.0.0"
    check(
        "norm exposes a real version mismatch",
        norm(other_mi) != norm(other_asm)[: len(norm(other_mi))],
    )

    if failures:
        print(f"FAIL: {len(failures)} check_version selftest check(s)", file=sys.stderr)
        return 1
    print("PASS: check_version selftest")
    return 0


def main() -> int:
    fails = []

    # A gate must report a missing input as a FAIL line, not die on a traceback
    # hiding which of its checks could not run.
    def read_or(path: Path) -> str | None:
        try:
            return path.read_text(encoding="utf-8")
        except OSError as ex:
            fails.append(f"{path.name}: unreadable ({ex})")
            return None

    mi_src = read_or(MODINFO)
    mi = modinfo_version(MODINFO) if mi_src is not None else None
    if mi_src is not None and mi is None:
        fails.append("ModInfo.xml: no Version value")
    di = None
    di_src = read_or(DIST_MODINFO) if DIST_MODINFO.exists() else None
    if di_src is not None:
        di = modinfo_version(DIST_MODINFO)

    asm_src = read_or(ASSEMBLY)
    asm_m = (
        re.search(r'AssemblyVersion\("([0-9.]+)"\)', asm_src) if asm_src is not None else None
    )
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

    changelog_src = read_or(CHANGELOG) if CHANGELOG.exists() else None
    if mi and (changelog_src is None or mi not in changelog_src):
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
    if argv == ["--selftest"]:
        raise SystemExit(_selftest())
    if argv:
        print(f"check_version.py: unrecognized arguments: {' '.join(argv)}", file=sys.stderr)
        print(USAGE, file=sys.stderr)
        raise SystemExit(2)
    sys.exit(main())
