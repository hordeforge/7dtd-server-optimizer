#!/usr/bin/env python3
"""Structural proof: dedicated RE coverage docs + dump evidence exist and are IL-backed.

Drives real repo artifacts under research/docs and research/il (no hard-coded
game constants as the pass condition; asserts files and dump-backed markers).
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOCS = ROOT / "research" / "docs"
IL = ROOT / "research" / "il"

FAMILY_DOCS = [
    "coverage.md",
    "loop.md",
    "world-chunks.md",
    "terrain-height.md",
    "entity-ai.md",
    "network.md",
    "save-region.md",
    "managers.md",
    "light-mesh-water.md",
    "residuals.md",
    "INDEX.md",
]

# Product RealEarth docs (not under research/docs)
PRODUCT_DOCS = ROOT / "7days-realworld" / "docs"
PRODUCT_REALEARTH = [
    "realearth-runtime.md",
    "realearth-surfaces.md",
    "realearth-review.md",
]

DUMP_SETS = [
    "gmUpdate-v3.0.1",
    "deep-v3.0.1",
    "deeper-v3.0.1",
    "gaps-v3.0.1",
    "loop-complete-v3.0.1",
    "terrain-stock-v3.0.1",
    "realearth-surfaces-v3.0.1",
    "dedi-complete-v3.0.1",
]

TOOLS = [
    "DumpGmUpdate.cs",
    "DumpDeep.cs",
    "DumpDeeper.cs",
    "DumpGaps.cs",
    "DumpLoopComplete.cs",
    "DumpTerrain.cs",
    "DumpRealEarthSurfaces.cs",
    "DumpDediComplete.cs",
]


def has_il_backed_claim(text: str) -> bool:
    if "research/il/" in text or "../il/" in text or "il/" in text:
        if re.search(
            r"IL\s*=\s*\d+|IL=\d+|y\s*>>\s*2|ldc\.i4|ChunkBlockYDim|ticksPerSecond",
            text,
        ):
            return True
        if re.search(r"_il\.txt|_calls\.md|DEDI_COMPLETE|TERRAIN_auto|SAVE_LIGHT", text):
            return True
    if re.search(r"\bIL\s*=\s*\*?\*?\d+|IL=\d+", text):
        return True
    return bool(
        re.search(r"\bIL\b.*\d{2,}", text)
        and ("dump" in text.lower() or "il/" in text.lower() or "measured" in text.lower())
    )


def main() -> int:
    tools = ROOT / "7dtd-optimizer" / "tools"
    fails: list[str] = []

    for name in FAMILY_DOCS:
        p = DOCS / name
        if not p.is_file() or p.stat().st_size < 200:
            fails.append(f"missing/tiny doc: {p}")
            continue
        text = p.read_text(encoding="utf-8", errors="replace")
        if name in ("INDEX.md", "residuals.md", "coverage.md"):
            continue
        if not has_il_backed_claim(text):
            if not re.search(r"IL\s*=\s*\d+|IL=\d+", text):
                fails.append(f"no IL-backed claim: {name}")

    cov = (DOCS / "coverage.md").read_text(encoding="utf-8", errors="replace")
    for fam in (
        "Frame",
        "chunk",
        "Terrain",
        "AI",
        "Network",
        "Save",
        "Origin",
        "Manager",
        "Light",
        "ModEvents",
    ):
        if fam.lower() not in cov.lower():
            fails.append(f"coverage hub missing family keyword: {fam}")

    for ds in DUMP_SETS:
        d = IL / ds
        if not d.is_dir():
            fails.append(f"missing dump set dir: {ds}")
            continue
        if sum(1 for _ in d.iterdir()) < 1:
            fails.append(f"empty dump set: {ds}")

    auto = IL / "dedi-complete-v3.0.1" / "DEDI_COMPLETE_auto.md"
    if not auto.is_file():
        fails.append("missing DEDI_COMPLETE_auto.md")
    else:
        t = auto.read_text(encoding="utf-8", errors="replace")
        for needle in (
            "ModEvents",
            "NetPackage",
            "WorldState",
            "ChunkBlockYDim",
            "Origin.FixedUpdate",
        ):
            if needle not in t:
                fails.append(f"DEDI_COMPLETE_auto missing {needle}")

    for tool in TOOLS:
        if not (tools / tool).is_file():
            fails.append(f"missing dump tool: {tool}")

    res = (DOCS / "residuals.md").read_text(encoding="utf-8", errors="replace")
    if "unmapped" not in res.lower() and "closed" not in res.lower():
        fails.append("residuals missing coverage-closed language")
    if re.search(r"(?i)managed surface.*(TODO|not reversed|not started)", res):
        fails.append("residuals still marks managed surface incomplete")

    ban_patterns = [
        (r"(?i)WorldState.*still partially open", "WorldState still open"),
        (r"(?i)Dedicated path is \*\*not a no-op\*\*", "Origin dedicated wrong"),
        (r"(?i)GAME_LOOP open gap #8", "stale GAME_LOOP gap #8"),
    ]
    for name, base in (
        ("realearth-surfaces.md", PRODUCT_DOCS),
        ("terrain-height.md", DOCS),
        ("loop.md", DOCS),
    ):
        text = (base / name).read_text(encoding="utf-8", errors="replace")
        for pat, label in ban_patterns:
            if re.search(pat, text):
                fails.append(f"{name}: banned open-gap language ({label})")

    # no leftover old filenames in research docs
    old_names = [
        "GAME_LOOP.md",
        "STRUCTURE_DEEP.md",
        "DEDICATED_ENGINE_COVERAGE.md",
        "SYNTHESIS_deeper.md",
    ]
    for name in FAMILY_DOCS + ["INDEX.md", "coverage.md"]:
        text = (DOCS / name).read_text(encoding="utf-8", errors="replace")
        for old in old_names:
            if old in text:
                fails.append(f"{name} still references {old}")

    # RealEarth product docs live under 7days-realworld/docs (not research/docs)
    for name in PRODUCT_REALEARTH:
        p = PRODUCT_DOCS / name
        if not p.is_file() or p.stat().st_size < 200:
            fails.append(f"missing product RealEarth doc: {p}")
        if (DOCS / name).exists():
            fails.append(f"RealEarth doc still under research/docs: {name}")

    # research INDEX should not own product RealEarth as primary
    idx = (DOCS / "INDEX.md").read_text(encoding="utf-8", errors="replace")
    if "generic engine" not in idx.lower() and "Generic engine" not in idx:
        fails.append("research INDEX missing generic-engine ownership language")
    if "7days-realworld/docs" not in idx:
        fails.append("research INDEX should link product RealEarth docs")

    if fails:
        print("FAIL:")
        for f in fails:
            print(" -", f)
        return 1
    print("OK: dedi coverage docs + dump sets + tools present")
    print(f"  docs_checked={len(FAMILY_DOCS)} dump_sets={len(DUMP_SETS)} tools={len(TOOLS)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
