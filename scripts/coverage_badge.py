#!/usr/bin/env python3
"""Render the line-coverage badge SVG from a Cobertura XML report.

Run: python3 scripts/coverage_badge.py COBERTURA_XML OUTPUT.svg
     python3 scripts/coverage_badge.py --selftest     (wired into `make test`)
"""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from decimal import ROUND_HALF_UP, Decimal, InvalidOperation
from pathlib import Path

from selftest_support import Checks

# Spelled the way every script in this directory is invoked (`python3
# scripts/coverage_badge.py`), so the usage line and every error line name the
# same command the caller typed.
NAME = "scripts/coverage_badge.py"

USAGE = """\
usage: scripts/coverage_badge.py COBERTURA_XML OUTPUT.svg [--selftest] [-h | --help]

Render the README coverage badge from a Cobertura report (the one `make
coverage` writes); CI commits the resulting SVG. --selftest is wired into
`make test`.
  COBERTURA_XML  report to read the `line-rate` attribute from
  OUTPUT.svg     badge file to write
  --selftest  exercise the threshold/band logic and the report->badge path
              (a silently wrong threshold ships a misleading shield)
  -h, --help  show this help\
"""


def colour(pct: int) -> str:
    if pct >= 90:
        return "#4c1"
    if pct >= 75:
        return "#97ca00"
    if pct >= 60:
        return "#dfb317"
    if pct >= 40:
        return "#fe7d37"
    return "#e05d44"


def badge(pct: int, fill: str) -> str:
    lw, vw = 64, 36
    total = lw + vw
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{total}" height="20"'
        f' role="img" aria-label="coverage: {pct}%">\n'
        f"<title>coverage: {pct}%</title>\n"
        '<linearGradient id="s" x2="0" y2="100%">'
        '<stop offset="0" stop-color="#bbb" stop-opacity=".1"/>'
        '<stop offset="1" stop-opacity=".1"/>'
        "</linearGradient>\n"
        f'<clipPath id="r"><rect width="{total}" height="20" rx="3" fill="#fff"/></clipPath>\n'
        f'<g clip-path="url(#r)"><rect width="{lw}" height="20" fill="#555"/>'
        f'<rect x="{lw}" width="{vw}" height="20" fill="{fill}"/>'
        f'<rect width="{total}" height="20" fill="url(#s)"/></g>\n'
        '<g fill="#fff" text-anchor="middle"'
        ' font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">'
        f'<text x="{lw / 2}" y="14">coverage</text>'
        f'<text x="{lw + vw / 2}" y="14">{pct}%</text></g>\n'
        "</svg>\n"
    )


def percent(line_rate: str) -> int:
    """The badge percentage for a cobertura `line-rate`.

    Decimal, not float: `round(0.895 * 100)` in binary floating point is
    round(89.49999999999999) = 89, so a report of exactly 89.5% rendered one
    point low and dropped the badge from the green band into orange. Decimal
    reads the decimal literal the report already is, and ROUND_HALF_UP settles
    the tie deterministically instead of leaving it to binary representation.
    """
    return int(
        (Decimal(line_rate) * 100).quantize(Decimal(1), rounding=ROUND_HALF_UP)
    )


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(
            f"{NAME}: expected COBERTURA_XML and OUTPUT.svg,"
            f" got {len(argv) - 1}: {' '.join(argv[1:])}",
            file=sys.stderr,
        )
        print(USAGE, file=sys.stderr)
        return 2
    # Name the offending input and fail, like every sibling gate: a raw
    # traceback here would bury the fact that the COVERAGE REPORT is the thing
    # missing or malformed, and would read as a bug in this script.
    try:
        root = ET.parse(argv[1]).getroot()
    except (OSError, ET.ParseError) as ex:
        print(
            f"FAIL: {argv[1]} is not a readable Cobertura report: {ex}",
            file=sys.stderr,
        )
        return 1
    try:
        pct = percent(root.get("line-rate", "0"))
    except InvalidOperation:
        # A non-numeric line-rate ("" or an empty element attribute) must not
        # render as a 0% red shield: that is a silent, wrong badge, the exact
        # failure this script exists to prevent.
        print(
            f"FAIL: {argv[1]} has a non-numeric line-rate "
            f"({root.get('line-rate', '0')!r})",
            file=sys.stderr,
        )
        return 1
    # Pinned codec like every other text write in scripts/: the badge lands
    # on GitHub via CI, and a non-UTF-8 preferred locale must not change bytes.
    try:
        Path(argv[2]).write_text(badge(pct, colour(pct)), encoding="utf-8")
    except OSError as ex:
        print(f"{NAME}: cannot write OUTPUT.svg {argv[2]}: {ex}", file=sys.stderr)
        return 2
    return 0


def _selftest() -> int:
    """Pin badge rendering end to end on synthetic inputs.

    CI renders the README badge straight from cobertura XML, so a silently
    wrong threshold or percentage here ships as a misleading shield. Drive
    colour() on both sides of every boundary, badge()'s structure, and
    main() against a synthetic report including the missing-attribute
    default (line-rate absent -> 0 -> red).
    """
    import tempfile

    checks = Checks("coverage_badge")

    t = checks

    # Exact endpoints on BOTH sides of every threshold: a bound that quietly
    # tightens or loosens by one must fail here, not on the README badge.
    for pct, want, label in (
        (90, "#4c1", "90 green"),
        (89, "#97ca00", "89 below green"),
        (75, "#97ca00", "75 light green"),
        (74, "#dfb317", "74 below light green"),
        (60, "#dfb317", "60 yellow"),
        (59, "#fe7d37", "59 below yellow"),
        (40, "#fe7d37", "40 orange"),
        (39, "#e05d44", "39 below orange"),
        (0, "#e05d44", "0 red"),
        (100, "#4c1", "100 green"),
    ):
        t.check(f"colour boundary: {label}", colour(pct) == want)

    svg = badge(92, "#97ca00")
    t.check("badge labels the percentage", "coverage: 92%" in svg)
    t.check("badge paints the value rect with the fill colour", 'fill="#97ca00"' in svg)
    t.check("badge uses the fixed 64+36 layout", 'width="100" height="20"' in svg)

    # Percentage conversion, including the ties a binary float got wrong:
    # float(0.895) * 100 is 89.49999999999999, so the old float path rounded a
    # report of exactly 89.5% down to 89 and out of the green band.
    for rate, want_pct, label in (
        ("0.9234", 92, "ordinary rate rounds to nearest"),
        ("0.895", 90, "exact 89.5 tie rounds up, not down to 89"),
        ("0.705", 71, "exact 70.5 tie rounds up"),
        ("0.9", 90, "90% stays in the green band"),
        ("0.89999", 90, "just under 90% rounds into the band"),
        ("1", 100, "full coverage"),
        ("0", 0, "zero coverage"),
    ):
        t.check(f"percent: {label}", percent(rate) == want_pct)

    with tempfile.TemporaryDirectory(prefix="es-badge-test.") as td:
        out = Path(td) / "badge.svg"
        report = Path(td) / "coverage.cobertura.xml"
        report.write_text(
            '<coverage line-rate="0.9234" branch-rate="0"></coverage>',
            encoding="utf-8",
        )
        rc = main([sys.argv[0], str(report), str(out)])
        text = out.read_text(encoding="utf-8")
        # percent(0.9234) == 92 -> green band (>= 90).
        t.check("main exits 0 on a well-formed report", rc == 0)
        t.check("main renders the rounded percentage", 'coverage: 92%' in text)
        t.check(
            "main picks the band colour for 92",
            'fill="#4c1"' in text,
        )
        # A non-numeric line-rate must fail loudly, not render a badge from
        # whatever the conversion produced.
        junk = Path(td) / "junk.cobertura.xml"
        junk.write_text('<coverage line-rate="n/a"></coverage>', encoding="utf-8")
        rc_junk = main([sys.argv[0], str(junk), str(out)])
        t.check("main rejects a non-numeric line-rate", rc_junk == 1)
        # A report without line-rate must read as 0% red, not crash or lie.
        bare = Path(td) / "bare.cobertura.xml"
        bare.write_text("<coverage></coverage>", encoding="utf-8")
        rc_bare = main([sys.argv[0], str(bare), str(out)])
        text_bare = out.read_text(encoding="utf-8")
        t.check("main treats a missing line-rate as exit 0", rc_bare == 0)
        t.check(
            "missing line-rate renders 0% red",
            'coverage: 0%' in text_bare and '#e05d44' in text_bare,
        )
        # An unwritable OUTPUT.svg is the caller's bad argument (exit 2), one
        # line on stderr, no traceback.
        t.check(
            "main exits 2 when the badge cannot be written",
            main([sys.argv[0], str(report), str(Path(td) / "no-such-dir/b.svg")]) == 2,
        )

        # A MISSING or MALFORMED report is an operator error, not a crash: exit
        # 1 with the offending path named, and above all no badge written.
        # Before this the parse raised a traceback, and a non-numeric
        # line-rate rendered as a 0% red shield - a silently wrong badge.
        missing = Path(td) / "not-there.cobertura.xml"
        rc_missing = main([sys.argv[0], str(missing), str(out)])
        t.check("missing report exits 1 without a traceback", rc_missing == 1)
        malformed = Path(td) / "malformed.cobertura.xml"
        malformed.write_text("<coverage line-rate=", encoding="utf-8")
        rc_malformed = main([sys.argv[0], str(malformed), str(out)])
        t.check("malformed report exits 1", rc_malformed == 1)
        notanumber = Path(td) / "notanumber.cobertura.xml"
        notanumber.write_text(
            '<coverage line-rate="n/a"></coverage>', encoding="utf-8"
        )
        before = out.read_text(encoding="utf-8")
        rc_nan = main([sys.argv[0], str(notanumber), str(out)])
        t.check("non-numeric line-rate exits 1", rc_nan == 1)
        t.check(
            "non-numeric line-rate leaves the old badge untouched",
            out.read_text(encoding="utf-8") == before,
        )

    return checks.finish()


if __name__ == "__main__":
    argv = sys.argv[1:]
    if argv in (["-h"], ["--help"]):
        print(USAGE)
        raise SystemExit(0)
    if argv == ["--selftest"]:
        raise SystemExit(_selftest())
    raise SystemExit(main(sys.argv))
