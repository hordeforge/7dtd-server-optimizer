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
5. The CHANGELOG's released sections parse, run newest-first without repeats,
   and the newest one is the shipped mod version (a bump with no notes, or
   notes for a version the manifest does not carry, is caught).

Run: python3 scripts/check_version.py
     python3 scripts/check_version.py --selftest     (both wired into `make test`)
"""
from __future__ import annotations

import re
import sys
from itertools import pairwise
from pathlib import Path

from cli_common import run_cli
from repo_root import repo_root
from selftest_support import Checks

ROOT = repo_root()
MODINFO = ROOT / "Source" / "EfficientServer" / "ModInfo.xml"
DIST_MODINFO = ROOT / "dist" / "EfficientServer" / "ModInfo.xml"
ASSEMBLY = ROOT / "Source" / "EfficientServer" / "AssemblyInfo.cs"
CHANGELOG = ROOT / "CHANGELOG.md"
DOCS = ROOT / "docs"

USAGE = """\
usage: check_version.py [--selftest] [-h | --help]

Gate: ModInfo.xml, AssemblyInfo.cs and the dist ModInfo must carry consistent
versions, docs must not claim a version newer than shipped, and CHANGELOG.md
must mention the shipped version in a well-formed, newest-first release
section list. Wired into `make test`.
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


# `## [1.19.0] - 2026-09-20` is a released section; `## [Unreleased]` is the
# staging section; `## Version numbering` is prose above the list. Anything
# else at that level is not a release record.
_SECTION_RE = re.compile(
    r"^## \[(?P<label>[^\]]+)\](?:\s+-\s+(?P<date>\S+))?\s*$", re.MULTILINE
)


def changelog_sections(text: str) -> list[tuple[str, str | None]]:
    """Every `## [label] - date` section in file order, as (label, date)."""
    return [(m.group("label"), m.group("date")) for m in _SECTION_RE.finditer(text)]


def released_sections(text: str) -> list[tuple[str, str | None]]:
    """Version-numbered sections in file order, dropping `[Unreleased]`."""
    return [
        (label, date)
        for label, date in changelog_sections(text)
        if re.fullmatch(r"[0-9]+(?:\.[0-9]+)+", label)
    ]


def _changelog_fails(text: str, shipped: str) -> list[str]:
    """Structure of the release list against the version the mod reports.

    A release bump moves ModInfo and adds a dated section in the same commit;
    anything else leaves the notes and the shipped version disagreeing about
    what shipped, which is the drift this gate exists to stop.
    """
    fails: list[str] = []
    released = released_sections(text)
    if not released:
        return ["CHANGELOG.md has no `## [X.Y.Z] - date` release section"]

    seen: list[tuple[int, ...]] = []
    for label, date in released:
        if not date:
            fails.append(f"CHANGELOG.md section [{label}] has no release date")
        if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)+", label):
            fails.append(f"CHANGELOG.md section [{label}] is not a version number")
            continue
        version = norm(label)
        if version in seen:
            fails.append(f"CHANGELOG.md repeats release section [{label}]")
        seen.append(version)

    # Newest first: every section must be a strictly lower version than the
    # one above it.
    for above, below in pairwise(seen):
        if above <= below:
            fails.append(
                "CHANGELOG.md release sections are not newest-first "
                f"({'.'.join(str(p) for p in below)} above "
                f"{'.'.join(str(p) for p in above)})"
            )
            break

    newest = norm(released[0][0])
    if newest != norm(shipped):
        fails.append(
            f"CHANGELOG.md newest release section is [{released[0][0]}], "
            f"but the mod reports {shipped}"
        )
    return fails


def _selftest() -> int:
    """Pin the extraction/normalization primitives the consistency checks use.

    main() reads fixed repo paths, so the selftest drives its pure helpers on
    synthetic inputs: a synthetic ModInfo.xml for modinfo_version and literal
    strings for norm(), including the trailing-'.0' equivalence rule the
    ModInfo-vs-AssemblyVersion comparison depends on.
    """
    import tempfile

    checks = Checks("check_version")

    t = checks

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
        t.check("modinfo_version reads the Version attribute", modinfo_version(mi) == "1.17.0")
        no_version = Path(td) / "NoVersion.xml"
        no_version.write_text('<xml>\n\t<Name value="X" />\n</xml>\n', encoding="utf-8")
        t.check(
            "modinfo_version returns None when Version is missing",
            modinfo_version(no_version) is None,
        )

    t.check("norm splits numeric parts", norm("1.17.0") == (1, 17, 0))
    # The shipped pair: ModInfo "1.17.0" vs AssemblyVersion "1.17.0.0". The
    # comparison in main() truncates the assembly tuple to the ModInfo length;
    # pin both the equal and the drifted outcome of exactly that expression.
    mi_v, asm_v = "1.17.0", "1.17.0.0"
    t.check("norm treats trailing .0 as cosmetic", norm(mi_v) == norm(asm_v)[: len(norm(mi_v))])
    other_mi, other_asm = "1.18.0", "1.17.0.0"
    t.check(
        "norm exposes a real version mismatch",
        norm(other_mi) != norm(other_asm)[: len(norm(other_mi))],
    )

    # The release-list gate, on synthetic changelogs: the repo gate above only
    # fails on tree drift, so a broken section regex would stay green here.
    good = (
        "## Version numbering\n\nprose\n\n"
        "## [Unreleased]\n\n### Added\n- thing\n\n"
        "## [1.19.0] - 2026-09-20\n\n### Fixed\n- thing\n\n"
        "## [1.18.0] - 2026-09-11\n\n### Fixed\n- thing\n"
    )
    t.check(
        "_changelog_fails accepts a well-formed newest-first list",
        _changelog_fails(good, "1.19.0") == [],
    )
    t.check(
        "released_sections drops Unreleased and prose headings",
        [label for label, _ in released_sections(good)] == ["1.19.0", "1.18.0"],
    )
    # A version bump that never reached the notes: manifest ahead of the list.
    t.check(
        "_changelog_fails catches a manifest bump with no release section",
        any("1.20.0" in f for f in _changelog_fails(good, "1.20.0")),
    )
    # Notes for a release the manifest does not carry.
    t.check(
        "_changelog_fails catches notes ahead of the manifest",
        any("1.19.0" in f for f in _changelog_fails(good, "1.18.0")),
    )
    undated = "## [1.19.0]\n\n### Fixed\n- thing\n"
    t.check(
        "_changelog_fails rejects an undated release section",
        any("no release date" in f for f in _changelog_fails(undated, "1.19.0")),
    )
    repeated = "## [1.19.0] - 2026-09-20\n\nx\n\n## [1.19.0] - 2026-09-19\n\nx\n"
    t.check(
        "_changelog_fails rejects a repeated release section",
        any("repeats" in f for f in _changelog_fails(repeated, "1.19.0")),
    )
    ascending = "## [1.18.0] - 2026-09-11\n\nx\n\n## [1.19.0] - 2026-09-20\n\nx\n"
    t.check(
        "_changelog_fails rejects an oldest-first list",
        any("newest-first" in f for f in _changelog_fails(ascending, "1.19.0")),
    )
    t.check(
        "_changelog_fails rejects a changelog with no release section",
        _changelog_fails("## [Unreleased]\n\n- thing\n", "1.19.0") != [],
    )

    return checks.finish()


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
    if mi and changelog_src is not None:
        fails.extend(_changelog_fails(changelog_src, mi))

    if fails:
        print("FAIL:", file=sys.stderr)
        for fail in fails:
            print(f"  {fail}", file=sys.stderr)
        return 1
    print(f"OK: versions consistent (ModInfo {mi}, Assembly {asm}, dist {di})")
    return 0


if __name__ == "__main__":
    run_cli("check_version.py", USAGE, main, _selftest)
