#!/usr/bin/env python3
"""Render the line-coverage badge SVG from a Cobertura XML report."""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path


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


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(f"usage: {argv[0]} COBERTURA_XML OUTPUT.svg", file=sys.stderr)
        return 2
    root = ET.parse(argv[1]).getroot()
    pct = round(float(root.get("line-rate", "0")) * 100)
    # Pinned codec like every other text write in scripts/: the badge lands
    # on GitHub via CI, and a non-UTF-8 preferred locale must not change bytes.
    Path(argv[2]).write_text(badge(pct, colour(pct)), encoding="utf-8")
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

    failures: list[str] = []

    def check(name: str, cond: bool) -> None:
        if cond:
            print("PASS: " + name)
        else:
            print("FAIL: " + name, file=sys.stderr)
            failures.append(name)

    if sys.argv[1:] != ["--selftest"]:
        print(f"usage: {Path(sys.argv[0]).name} --selftest", file=sys.stderr)
        return 2

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
        check(f"colour boundary: {label}", colour(pct) == want)

    svg = badge(92, "#97ca00")
    check("badge labels the percentage", "coverage: 92%" in svg)
    check("badge paints the value rect with the fill colour", 'fill="#97ca00"' in svg)
    check("badge uses the fixed 64+36 layout", 'width="100" height="20"' in svg)

    with tempfile.TemporaryDirectory(prefix="es-badge-test.") as td:
        out = Path(td) / "badge.svg"
        report = Path(td) / "coverage.cobertura.xml"
        report.write_text(
            '<coverage line-rate="0.9234" branch-rate="0"></coverage>',
            encoding="utf-8",
        )
        rc = main([sys.argv[0], str(report), str(out)])
        text = out.read_text(encoding="utf-8")
        # round(0.9234 * 100) == 92 -> green band (>= 90).
        check("main exits 0 on a well-formed report", rc == 0)
        check("main renders the rounded percentage", 'coverage: 92%' in text)
        check(
            "main picks the band colour for 92",
            'fill="#4c1"' in text,
        )
        # A report without line-rate must read as 0% red, not crash or lie.
        bare = Path(td) / "bare.cobertura.xml"
        bare.write_text("<coverage></coverage>", encoding="utf-8")
        rc_bare = main([sys.argv[0], str(bare), str(out)])
        text_bare = out.read_text(encoding="utf-8")
        check("main treats a missing line-rate as exit 0", rc_bare == 0)
        check(
            "missing line-rate renders 0% red",
            'coverage: 0%' in text_bare and '#e05d44' in text_bare,
        )

    if failures:
        print(f"FAIL: {len(failures)} coverage_badge selftest check(s)", file=sys.stderr)
        return 1
    print("PASS: coverage_badge selftest")
    return 0


if __name__ == "__main__":
    raise SystemExit(_selftest() if "--selftest" in sys.argv[1:] else main(sys.argv))
