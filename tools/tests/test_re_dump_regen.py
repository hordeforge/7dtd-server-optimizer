#!/usr/bin/env python3
"""Regression test: Cecil dumpers regenerate non-empty RE artifacts from local dedicated Assembly-CSharp.

Requires:
  - 7 Days to Die Dedicated Server install with Managed/Assembly-CSharp.dll
  - mcs + mono on PATH
  - Mono.Cecil.dll next to tools/

Does not redistribute game IL; writes only under a caller-supplied out dir or
tools/tests/_out (gitignored).
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
CECIL = TOOLS / "Mono.Cecil.dll"
DUMPER = TOOLS / "DumpFrameEntries.cs"
EXE = TOOLS / "DumpFrameEntries.exe"


def find_asm() -> Path | None:
    env = os.environ.get("SEVENDTD_DS_DIR") or os.environ.get("SEVENDTD_ASM")
    candidates = []
    if env:
        p = Path(env)
        if p.is_file() and p.name.endswith(".dll"):
            candidates.append(p)
        else:
            candidates.append(p / "7DaysToDieServer_Data/Managed/Assembly-CSharp.dll")
    home = Path.home()
    candidates.extend(
        [
            home
            / ".local/share/Steam/steamapps/common/7 Days to Die Dedicated Server/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll",
            home
            / ".steam/steam/steamapps/common/7 Days to Die Dedicated Server/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll",
        ]
    )
    for c in candidates:
        if c.is_file():
            return c
    return None


def main() -> int:
    asm = find_asm()
    if asm is None:
        print("SKIP: dedicated Assembly-CSharp.dll not found (set SEVENDTD_DS_DIR)")
        return 0
    if not CECIL.is_file():
        print("FAIL: missing Mono.Cecil.dll at", CECIL, file=sys.stderr)
        return 1
    if not DUMPER.is_file():
        print("FAIL: missing", DUMPER, file=sys.stderr)
        return 1

    out = Path(os.environ.get("RE_DUMP_OUT", TOOLS / "tests" / "_out" / "frame-entries"))
    out.mkdir(parents=True, exist_ok=True)

    compile_cmd = ["mcs", f"-r:{CECIL}", f"-out:{EXE}", str(DUMPER)]
    print("RUN:", " ".join(compile_cmd))
    subprocess.check_call(compile_cmd, cwd=str(TOOLS))

    run_cmd = ["mono", str(EXE), str(asm), str(out)]
    print("RUN:", " ".join(run_cmd))
    subprocess.check_call(run_cmd, cwd=str(TOOLS))

    required = [
        out / "inventory-frame-entries.md",
        out / "inventory-gmupdate-calls.md",
        out / "inventory-manager-updates.md",
    ]
    for f in required:
        if not f.is_file() or f.stat().st_size < 50:
            print("FAIL: missing or tiny", f, file=sys.stderr)
            return 1
        text = f.read_text(encoding="utf-8", errors="replace")
        if f.name == "inventory-frame-entries.md":
            for needle in ("GameManager", "ConnectionManager", "DynamicMeshManager", "Update"):
                if needle not in text:
                    print("FAIL:", f, "missing", needle, file=sys.stderr)
                    return 1
        if f.name == "inventory-gmupdate-calls.md":
            for needle in ("UpdateTick", "gmUpdate", "ThreadManager"):
                if needle not in text:
                    print("FAIL:", f, "missing", needle, file=sys.stderr)
                    return 1

    print("OK: regenerated", out)
    for f in required:
        print(" ", f.name, f.stat().st_size, "bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
