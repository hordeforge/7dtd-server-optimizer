#!/usr/bin/env python3
"""Regression gate: the shipped mod version must be consistent across sources.

Checks that:
1. Source/EfficientServer/ModInfo.xml and, when dist/ has been packaged,
   dist/EfficientServer/ModInfo.xml carry the same version (a tree that has
   never packaged has no dist copy to compare, so that half is skipped).
2. AssemblyInfo.cs AssemblyVersion matches ModInfo (ModInfo "1.17.0" ==
   Assembly "1.17.0.0", trailing ".0" parts ignored).
3. docs/ claim no version newer than the shipped one (catches the v1.18
   drift class where docs referenced a release that never shipped).
4. CHANGELOG.md exists and mentions the shipped mod version, so a release
   cannot tag without its changelog entry.
5. The CHANGELOG's released sections parse, carry a real ISO release date,
   run newest-first by both version and date without repeats, and the newest
   one is the shipped mod version (a bump with no notes, a two-digit year, a
   date that does not exist, or notes for a version the manifest does not
   carry, is caught). Each section also carries at most one heading per impact
   level, from the known set, so a release's entries cannot be split across
   two `### Fixed` blocks or hidden in a misspelled group.
6. The shipped version has a row in the docs/RESULTS.md version-history
   table, so a release cannot ship with a version history that stops at the
   release before it.
7. SECURITY.md's supported-versions section names the shipped mod version and
   nothing newer, so the "which version still gets fixes" promise cannot go
   stale behind a bump.

Run: python3 scripts/check_version.py
     python3 scripts/check_version.py --selftest     (both wired into `make test`)
"""

from __future__ import annotations

import re
import sys
from datetime import date as date_type
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
usage: scripts/check_version.py [--selftest] [-h | --help]

Gate: ModInfo.xml, AssemblyInfo.cs and the dist ModInfo must carry consistent
versions, docs must not claim a version newer than shipped, CHANGELOG.md
must mention the shipped version in a well-formed, newest-first release
section list, and SECURITY.md must name the shipped version as the supported
one. Wired into `make test`.
  --selftest  exercise the version extraction/normalization logic itself (the
              repo gate above only fails on tree drift; it stays green if this
              script's own matching logic silently breaks)
  -h, --help  show this help\
"""


def read_text(path: Path) -> str | None:
    """UTF-8 text of ``path``, or None when it cannot be read at all.

    Every input here is a file in the working tree that a contributor edits,
    and the byte content is not pinned by anything: a stray latin-1 byte
    saved by a Windows editor is enough. The gate's contract is to report
    such a file as a FAIL line naming it, so an undecodable byte returns
    None here instead of raising UnicodeDecodeError out of the caller and
    replacing the whole report with a traceback.
    """
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None


def version_parsable(value: str) -> bool:
    """True when `norm` can parse `value` without raising.

    Both version regexes are `[0-9.]+`, which also matches a leading, trailing
    or doubled dot. `norm` runs int() per component, so "1.17.0." raises
    ValueError; main() has no handler, so a one-character typo in ModInfo.xml
    would replace the whole report with a traceback. Reject the shape here and
    let the missing value reach the FAIL path like any other.
    """
    return not (value.startswith(".") or value.endswith(".") or ".." in value)


def modinfo_version(path: Path) -> str | None:
    text = read_text(path)
    if text is None:
        return None
    m = re.search(r'Version\s+value="([0-9.]+)"', text)
    if m is None or not version_parsable(m.group(1)):
        return None
    return m.group(1)


# Longest spelling the gates see: AssemblyVersion "X.Y.Z.W" (ModInfo is X.Y.Z).
_VERSION_PARTS = 4


def norm(v: str) -> tuple[int, ...]:
    """Version as a fixed-width component tuple, so a short form compares equal
    to its padded one.

    Bare `tuple(int(x) for x in v.split("."))` made `1.20` and `1.20.0` DIFFERENT
    versions: `(1, 20) < (1, 20, 0)`, so the release-list gate accepted a
    changelog carrying both, and `norm(mi) != norm(asm)[: len(norm(mi))]`
    truncated the assembly version instead of padding it, reading a real
    `1.17.0.5` assembly as the `1.17.0` manifest. Padding every version to the
    four components the longest spelling uses (ModInfo X.Y.Z, AssemblyVersion
    X.Y.Z.W) makes the trailing zeros cosmetic on both sides.
    """
    parts = [int(x) for x in v.split(".")]
    return tuple(parts + [0] * (_VERSION_PARTS - len(parts)))


# `## [1.19.0] - 2026-09-20` is a released section; `## [Unreleased]` is the
# staging section; `## Version numbering` is prose above the list. Anything
# else at that level is not a release record.
_SECTION_RE = re.compile(r"^## \[(?P<label>[^\]]+)\](?:\s+-\s+(?P<date>\S+))?\s*$", re.MULTILINE)

# Impact groups a release section may split its entries into. Keep a matching
# heading, `Fixed` or `Breaking` included, and a release carrying two of them is
# a release whose entries are split in two: a reader scanning for one kind of
# change reads the first list and misses the rest.
_IMPACT_GROUPS = ("Added", "Changed", "Fixed", "Breaking", "Removed", "Deprecated")
_HEADING_RE = re.compile(r"^### (?P<title>.+?)\s*$", re.MULTILINE)


def changelog_sections(text: str) -> list[tuple[str, str | None]]:
    """Every `## [label] - date` section in file order, as (label, date)."""
    return [(m.group("label"), m.group("date")) for m in _SECTION_RE.finditer(text)]


def _impact_group_fails(text: str) -> list[str]:
    """Each release section must carry one heading per impact level.

    A release section runs from its `## [X.Y.Z]` heading to the next one, with
    the `[Unreleased]` staging area included. Two subsections of the same impact
    level split that level's entries in half with nothing in between saying so
    (the [Unreleased] staging section grew a second `### Fixed` and the second
    list was a screen further down), and a heading outside the known set
    (`### Breakin`) silently opens a group no reader scans. Both are cheap to
    write and impossible to spot in review, which is what a gate is for.
    """
    fails: list[str] = []
    bounds = [m.start() for m in _SECTION_RE.finditer(text)] + [len(text)]
    for section, start, end in zip(
        _SECTION_RE.finditer(text), bounds[:-1], bounds[1:], strict=True
    ):
        label = section.group("label")
        body = text[start:end]
        seen: set[str] = set()
        for title in (m.group("title") for m in _HEADING_RE.finditer(body)):
            if title not in _IMPACT_GROUPS:
                fails.append(
                    f"CHANGELOG.md section [{label}] has a `### {title}` heading, "
                    f"which is not one of {', '.join(_IMPACT_GROUPS)}"
                )
            elif title in seen:
                fails.append(f"CHANGELOG.md section [{label}] repeats the `### {title}` group")
            else:
                seen.add(title)
    return fails


def released_sections(text: str) -> list[tuple[str, str | None]]:
    """Version-numbered sections in file order, dropping `[Unreleased]`."""
    return [
        (label, date)
        for label, date in changelog_sections(text)
        if re.fullmatch(r"[0-9]+(?:\.[0-9]+)+", label)
    ]


def release_date(date: str) -> date_type | None:
    """The section's release date as a real calendar date, or None.

    `date.fromisoformat` is the whole point: it rejects a two-digit year
    (`26-09-20`, which parses as year 26 and sorts before every real release)
    and an impossible calendar day (`2026-02-30`, `2026-13-01`) that a bare
    non-empty check waves through. Timezone-free by construction, so the
    comparison below never depends on the runner's TZ.
    """
    try:
        return date_type.fromisoformat(date)
    except ValueError:
        return None


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

    fails.extend(_impact_group_fails(text))

    seen: list[tuple[int, ...]] = []
    dates: list[tuple[str, date_type]] = []
    for label, date in released:
        parsed = release_date(date) if date else None
        if not date:
            fails.append(f"CHANGELOG.md section [{label}] has no release date")
        elif parsed is None:
            fails.append(
                f"CHANGELOG.md section [{label}] has an unparseable release "
                f"date {date!r} (expected ISO YYYY-MM-DD)"
            )
        else:
            dates.append((label, parsed))
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

    # Dates descend with the versions. A list whose dates run the other way
    # (or repeat a day across two releases) describes a release history the
    # changelog does not contain, and any reader dating a regression from it
    # lands on the wrong release.
    for (newer, d_newer), (older, d_older) in pairwise(dates):
        if d_older >= d_newer:
            fails.append(
                f"CHANGELOG.md release dates are not newest-first "
                f"([{older}] {d_older.isoformat()} above "
                f"[{newer}] {d_newer.isoformat()})"
            )
            break

    newest = norm(released[0][0])
    if newest != norm(shipped):
        fails.append(
            f"CHANGELOG.md newest release section is [{released[0][0]}], "
            f"but the mod reports {shipped}"
        )
    return fails


def _results_history_fails(text: str, shipped: str) -> list[str]:
    """The shipped version must appear in the docs version-history table.

    `docs/RESULTS.md` keeps a `## 0. Version history` table of every mod
    version and what it changed. The gate above only catches docs claiming a
    version *newer* than shipped, so a table that stops short of the newest
    release (it sat at 1.17.0 through 1.19.0) stayed green: a reader dating a
    regression from that table gets no row for the release they are running.
    """
    version = norm(shipped)
    m = re.search(
        r"^## 0\. Version history\s*$(?P<body>.*?)(?=^## |\Z)",
        text,
        re.MULTILINE | re.DOTALL,
    )
    if m is None:
        return ["RESULTS.md has no `## 0. Version history` section"]
    minor = f"{version[0]}.{version[1]}"
    # Boundary-anchored, not a bare substring: `in` on the rendered minor also
    # matches a table carrying only `| 1.170.0 |` or `| 11.17.0 |`, so a history
    # that stopped short of the shipped release stayed green. The lookarounds
    # keep a digit or dot on either side from satisfying the match.
    if not re.search(rf"(?<![0-9.]){re.escape(minor)}(?![0-9])", m.group("body")):
        return [
            (
                f"RESULTS.md version history has no row for {minor}"
                f" (shipped mod version is {shipped})"
            )
        ]
    return []


def _security_version_fails(text: str, shipped: str) -> list[str]:
    """SECURITY.md's supported-version statement must name the shipped mod version.

    That section is where an operator reads which version still gets fixes, and
    it spells the number out in prose ("1.19.0 at this writing"). Nothing else
    covered it: the docs/*.md scan looks for `v1.N` claims, which this file never
    uses, so a release bumped ModInfo and left the security promise naming the
    version before it. Older versions may also be named there (the 0.1.0
    artifact's `mod=1.17.0` is the worked example of the tag/mod split), and a
    tag inside a zip name is not a mod version at all, so a hyphenated token is
    skipped. The rule is the one that matters: the shipped version must be named
    and nothing newer may be.
    """
    m = re.search(
        r"^## Supported versions\s*$(?P<body>.*?)(?=^## |\Z)",
        text,
        re.MULTILINE | re.DOTALL,
    )
    if m is None:
        return ["SECURITY.md has no `## Supported versions` section"]
    # ASCII digits, spelled [0-9] rather than \d: Python's \d matches every
    # Unicode decimal digit and int() converts them, so a version claim written
    # with fullwidth or Arabic-Indic digits read as a real claim here. A gate
    # that a contributor can spell around is not a gate.
    claimed = {
        norm(tok) for tok in re.findall(r"(?<![-\w.])([0-9]+\.[0-9]+\.[0-9]+)\b", m.group("body"))
    }
    shipped_version = norm(shipped)
    if shipped_version not in claimed:
        return [
            (
                "SECURITY.md supported-versions section does not name the "
                f"shipped mod version {shipped}"
            )
        ]
    newer = sorted(v for v in claimed if v > shipped_version)
    if newer:
        names = ", ".join(".".join(str(p) for p in v) for v in newer)
        return [
            (
                f"SECURITY.md supported-versions section names {names}, "
                f"newer than the shipped {shipped}"
            )
        ]
    return []


def _selftest() -> int:
    """Pin the extraction/normalization primitives the consistency checks use.

    main() reads fixed repo paths, so the selftest drives its pure helpers on
    synthetic inputs: a synthetic ModInfo.xml for modinfo_version and literal
    strings for norm(), including the trailing-'.0' equivalence rule the
    ModInfo-vs-AssemblyVersion comparison depends on.
    """
    import tempfile

    t = Checks("check_version")

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
        # A file saved by a Windows editor as latin-1 is a real input here, and
        # it used to raise UnicodeDecodeError out of the gate, so one bad byte
        # replaced the whole version report with a traceback.
        latin1 = Path(td) / "Latin1.xml"
        latin1.write_bytes(
            b'<?xml version="1.0" encoding="UTF-8" ?>\n<xml>\n'
            b'\t<Name value="Caf\xe9" />\n'
            b'\t<Version value="1.17.0" />\n'
            b"</xml>\n"
        )
        t.check(
            "modinfo_version returns None on an undecodable byte, not a raise",
            read_text(latin1) is None and modinfo_version(latin1) is None,
        )
        # A trailing/leading/doubled dot passes the `[0-9.]+` Version regex but
        # makes norm() raise ValueError, and main() has no handler: the typo
        # this file exists to catch would replace the whole report with a
        # traceback. The malformed value must read as absent instead.
        for bad in ("1.17.0.", ".1.17.0", "1..17.0"):
            bad_xml = Path(td) / "BadVersion.xml"
            bad_xml.write_text(f'<xml>\n\t<Version value="{bad}" />\n</xml>\n', encoding="utf-8")
            t.check(
                f"modinfo_version rejects the malformed version {bad!r}, not a raise",
                modinfo_version(bad_xml) is None,
            )
            try:
                norm(bad)
                raised = False
            except ValueError:
                raised = True
            t.check(f"norm would raise on {bad!r} (the hazard being fenced)", raised)

    t.check("norm splits numeric parts", norm("1.17.0") == (1, 17, 0, 0))
    # The shipped pair: ModInfo "1.17.0" vs AssemblyVersion "1.17.0.0". The
    # trailing ".0" is cosmetic, and a 4th component that is NOT zero is a real
    # drift, not something truncation hides.
    mi_v, asm_v = "1.17.0", "1.17.0.0"
    t.check("norm treats trailing .0 as cosmetic", norm(mi_v) == norm(asm_v))
    t.check(
        "norm exposes a real fourth-component mismatch",
        norm(mi_v) != norm("1.17.0.5"),
    )
    t.check("norm pads a short version to a comparable width", norm("1.20") == norm("1.20.0"))
    other_mi, other_asm = "1.18.0", "1.17.0.0"
    t.check(
        "norm exposes a real version mismatch",
        norm(other_mi) != norm(other_asm),
    )

    # The release-list gate, on synthetic changelogs: the repo gate above only
    # fails on tree drift, so a broken section regex would stay green here.
    good = (
        "## Version numbering\n\nprose\n\n"
        "## [Unreleased]\n\n### Added\n- thing\n\n"
        "## [1.19.0] - 2026-09-20\n\n### Fixed\n- thing\n\n"
        "## [1.18.0] - 2026-09-11\n\n### Fixed\n- thing\n"
    )
    # One impact group split in two: the shape a release section grows by
    # accident when entries are appended to whichever group is open.
    repeated_group = (
        "## [1.19.0] - 2026-09-20\n\n### Added\n- a\n\n### Fixed\n- b\n\n### Fixed\n- c\n"
    )
    t.check(
        "_changelog_fails accepts a well-formed newest-first list",
        _changelog_fails(good, "1.19.0") == [],
    )
    t.check(
        "_changelog_fails accepts one heading per impact group",
        _impact_group_fails("## [1.19.0] - 2026-09-20\n\n### Fixed\n- a\n\n### Breaking\n- b\n")
        == [],
    )
    t.check(
        "_impact_group_fails catches a group repeated inside one section",
        any(
            "repeats" in f
            for f in _impact_group_fails(
                "## [Unreleased]\n\n### Fixed\n- a\n\n### Breaking\n- b\n\n### Fixed\n- c\n"
            )
        ),
    )
    # The same heading in two different sections is the normal case, not a repeat.
    t.check(
        "_impact_group_fails reads one section at a time",
        _impact_group_fails(
            "## [Unreleased]\n\n### Fixed\n- a\n\n## [1.19.0] - 2026-09-20\n\n### Fixed\n- b\n"
        )
        == [],
    )
    t.check(
        "_impact_group_fails catches a misspelled group",
        any(
            "not one of" in f
            for f in _impact_group_fails("## [1.19.0] - 2026-09-20\n\n### Breakin\n- a\n")
        ),
    )
    t.check(
        "_changelog_fails surfaces the group split in a release section",
        any("repeats" in f for f in _changelog_fails(repeated_group, "1.19.0")),
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
    # Same release, two spellings: `1.20` and `1.20.0` are one version, so the
    # list repeats itself and the newer one is not strictly above the older.
    respelled = "## [1.20.0] - 2026-09-20\n\nx\n\n## [1.20] - 2026-09-19\n\nx\n"
    t.check(
        "_changelog_fails rejects a release section repeated under another spelling",
        any("repeats" in f for f in _changelog_fails(respelled, "1.20.0")),
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

    # Date parsing: a two-digit year, an impossible calendar day and a
    # non-ISO spelling all reach the release list as "has a date" today.
    t.check(
        "release_date parses a full ISO date",
        release_date("2026-09-20") == date_type(2026, 9, 20),
    )
    for bad in ("26-09-20", "2026-02-30", "2026-13-01", "20/09/2026", "2026-9-2"):
        t.check(f"release_date rejects {bad}", release_date(bad) is None)
    t.check(
        "_changelog_fails rejects a two-digit release year",
        any(
            "unparseable release date" in f
            for f in _changelog_fails("## [1.19.0] - 26-09-20\n\nx\n", "1.19.0")
        ),
    )
    t.check(
        "_changelog_fails rejects an impossible release day",
        any(
            "unparseable release date" in f
            for f in _changelog_fails("## [1.19.0] - 2026-02-30\n\nx\n", "1.19.0")
        ),
    )
    same_day = "## [1.19.0] - 2026-09-20\n\nx\n\n## [1.18.0] - 2026-09-20\n\nx\n"
    t.check(
        "_changelog_fails rejects two releases sharing one date",
        any("newest-first" in f for f in _changelog_fails(same_day, "1.19.0")),
    )
    dated_backwards = "## [1.19.0] - 2026-09-01\n\nx\n\n## [1.18.0] - 2026-09-20\n\nx\n"
    t.check(
        "_changelog_fails rejects dates that ascend with the list",
        any("newest-first" in f for f in _changelog_fails(dated_backwards, "1.19.0")),
    )
    # A leap day is a real release date; it must not read as malformed.
    t.check(
        "_changelog_fails accepts a leap-day release",
        _changelog_fails("## [1.19.0] - 2028-02-29\n\nx\n", "1.19.0") == [],
    )

    # The docs version-history gate, on a synthetic RESULTS.md. The real file
    # is written in one table flavor (`1.18.0`, `1.16.0/1`, `1.14.0-3`), so a
    # match on the major.minor prefix has to survive all three.
    history = (
        "## 0. Version history\n\n| Ver | Change |\n|---|---|\n"
        "| 1.18.0/1 | x |\n| 1.16.0/1 | x |\n\n## 1. Shipped\n"
    )
    t.check(
        "_results_history_fails accepts a listed release",
        _results_history_fails(history, "1.18.0") == [],
    )
    t.check(
        "_results_history_fails accepts a collapsed row",
        _results_history_fails(history, "1.16.2") == [],
    )
    t.check(
        "_results_history_fails catches a table that stops short",
        any("no row" in f for f in _results_history_fails(history, "1.19.0")),
    )
    t.check(
        "_results_history_fails catches a missing section",
        _results_history_fails("## 1. Shipped\n", "1.19.0") != [],
    )
    # The version string must come from the table, not the rest of the file.
    outside = "## 0. Version history\n\n| Ver | Change |\n\n## 1. Shipped\n1.19.0\n"
    t.check(
        "_results_history_fails ignores text outside the table",
        _results_history_fails(outside, "1.19.0") != [],
    )
    # A substring test on the rendered minor also matches a table carrying only
    # `1.190.0` or `11.19.0`, so a history that never listed the shipped
    # release stayed green. The gate must be boundary-anchored on both sides.
    for decoy in ("1.190.0", "11.19.0", "2.1.19.0"):
        decoy_table = (
            f"## 0. Version history\n\n| Version | Change |\n| --- | --- |\n| {decoy} | x |\n"
        )
        t.check(
            f"_results_history_fails does not accept {decoy} as a row for 1.19",
            any("no row" in f for f in _results_history_fails(decoy_table, "1.19.0")),
        )
    t.check(
        "_results_history_fails accepts a real 1.19 row",
        _results_history_fails(
            "## 0. Version history\n\n| 1.19.0 | x |\n| 1.190.0 | y |\n", "1.19.0"
        )
        == [],
    )

    # The supported-versions statement, on a synthetic SECURITY.md. Older mod
    # versions and a tag inside a zip name may appear there; the shipped one
    # must be named and nothing newer may be.
    security = (
        "## Supported versions\n\nFixes are made for `ModInfo.xml` (1.19.0 at "
        "this writing). Older releases receive no backports, so\n"
        "`EfficientServer-0.1.0.zip` logging `mod=1.17.0` is correct.\n\n"
        "## Reporting\n\n1.19.0 is fine here.\n"
    )
    t.check(
        "_security_version_fails accepts the shipped version",
        _security_version_fails(security, "1.19.0") == [],
    )
    t.check(
        "_security_version_fails catches a stale supported version",
        any("does not name" in f for f in _security_version_fails(security, "1.20.0")),
    )
    t.check(
        "_security_version_fails catches a newer version promised as supported",
        any(
            "newer than the shipped" in f
            for f in _security_version_fails(
                "## Supported versions\n\nFixed for 1.19.0 and for the "
                "upcoming 1.20.0.\n\n## Reporting\n\nx\n",
                "1.19.0",
            )
        ),
    )
    t.check(
        "_security_version_fails reads the section, not the whole file",
        _security_version_fails(
            "## Supported versions\n\nThe current mod version is 1.19.0.\n\n"
            "## Reporting\n\ntry 1.99.0\n",
            "1.19.0",
        )
        == [],
    )
    t.check(
        "_security_version_fails catches a missing section",
        _security_version_fails("## Reporting\n\nx\n", "1.19.0") != [],
    )

    return t.finish()


def main() -> int:
    fails = []

    # A gate must report a missing input as a FAIL line, not die on a traceback
    # hiding which of its checks could not run. An undecodable byte is that
    # same class of input, so it is reported here rather than raised out of
    # read_text: the file is named and the rest of the checks still run.
    def read_or(path: Path) -> str | None:
        try:
            return path.read_text(encoding="utf-8")
        except UnicodeDecodeError as ex:
            fails.append(f"{path.name}: not valid UTF-8 ({ex})")
            return None
        except OSError as ex:
            fails.append(f"{path.name}: unreadable ({ex})")
            return None

    mi_src = read_or(MODINFO)
    mi = modinfo_version(MODINFO) if mi_src is not None else None
    if mi_src is not None and mi is None:
        fails.append("ModInfo.xml: no Version value")
    di = None
    di_src = read_or(DIST_MODINFO) if DIST_MODINFO.exists() else None
    di = modinfo_version(DIST_MODINFO) if di_src is not None else None

    asm_src = read_or(ASSEMBLY)
    asm_m = re.search(r'AssemblyVersion\("([0-9.]+)"\)', asm_src) if asm_src is not None else None
    asm = asm_m.group(1) if asm_m is not None and version_parsable(asm_m.group(1)) else None

    # trailing ".0" parts in the 4-part assembly version are cosmetic
    if mi and asm and norm(mi) != norm(asm):
        fails.append(f"ModInfo {mi} != AssemblyInfo {asm}")
    if di and mi and norm(di) != norm(mi):
        fails.append(f"dist ModInfo {di} != source ModInfo {mi}")

    if mi:
        shipped = norm(mi)
        # docs should not claim a future minor (v1.18 drift class); skip
        # changelog sections, which legitimately describe version history.
        # README/SECURITY/CONTRIBUTING carry the same `v1.N` claim form and
        # were outside the glob, so a stale one there read as current.
        for f in sorted(DOCS.glob("*.md")) + [
            ROOT / name
            for name in ("README.md", "SECURITY.md", "CONTRIBUTING.md")
            if (ROOT / name).exists()
        ]:
            txt = f.read_text(encoding="utf-8", errors="replace")
            body = txt.split("## Changelog", 1)[0]
            for m in re.finditer(r"v1\.([0-9]+)", body):
                minor = int(m.group(1))
                if minor > shipped[1]:
                    fails.append(f"{f.name}: claims v1.{minor} > shipped {mi}")

    changelog_src = read_or(CHANGELOG) if CHANGELOG.exists() else None
    if mi and (changelog_src is None or mi not in changelog_src):
        fails.append(f"CHANGELOG.md missing or has no entry for shipped mod version {mi}")
    if mi and changelog_src is not None:
        fails.extend(_changelog_fails(changelog_src, mi))

    results = ROOT / "docs" / "RESULTS.md"
    if mi and results.exists():
        fails.extend(_results_history_fails(results.read_text(encoding="utf-8"), mi))

    security = ROOT / "SECURITY.md"
    if mi and security.exists():
        fails.extend(_security_version_fails(security.read_text(encoding="utf-8"), mi))

    if fails:
        print("FAIL:", file=sys.stderr)
        for fail in fails:
            print(f"  {fail}", file=sys.stderr)
        return 1
    print(f"OK: versions consistent (ModInfo {mi}, Assembly {asm}, dist {di})")
    return 0


if __name__ == "__main__":
    run_cli("scripts/check_version.py", USAGE, main, _selftest)
