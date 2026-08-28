#!/usr/bin/env python3
"""Shared plumbing for the live-server harnesses in this directory.

validate_* / measure_* boot the same loadgen dedicated server and toggle the
same installed EfficientServer config, so the loadgen import-path setup, env-
driven path constants, timestamped logger, telnet readiness probe, and the
path-knob config swap live here once instead of drifting across copies.

Run via a sibling script (`python3 scripts/measure_es_onoff.py`); this module
is never an entry point itself.
"""
from __future__ import annotations

import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

from repo_root import repo_root

OPT_ROOT = repo_root()
LOADGEN_ROOT = OPT_ROOT.parent / "7dtd-loadgen"
sys.path.insert(0, str(LOADGEN_ROOT / "scripts"))

# These imports must stay below the sys.path insert (bloodmoon_profile lives
# only in the sibling loadgen tree; es_cfg_guard is local to this repo's
# scripts/, already on sys.path as the script directory); the resulting E402
# is exempted per-file in ruff.toml instead of inline noqa noise.
import bloodmoon_profile as B

from es_cfg_guard import ConfigSwap, write_atomic

# Public surface of this shared module (mypy no_implicit_reexport: consumers
# may import exactly these; B and write_atomic are deliberate re-exports).
__all__ = [
    "CFG_SWAP",
    "DEDICATED_CMDLINE_MARKER",
    "DS",
    "ES_CFG",
    "LOADGEN_CMDLINE_MARKER",
    "OUT_DIR",
    "B",
    "ensure_server_ready",
    "kill_matching_processes",
    "log",
    "teardown_bots",
    "write_atomic",
    "write_diag_config",
    "write_path_config",
    "write_report",
]

# Canonical here is SEVENDTD_DS_DIR (same var as build/install/run and the
# Makefile's DS=); SEVENDTD_SERVER_DIR is also accepted because that is the
# 7dtd-loadgen sibling's spelling (start_dedicated_prefab.sh), so one export
# drives both trees. An empty value falls through to the next candidate
# instead of selecting the current directory.
DS = Path(
    os.environ.get("SEVENDTD_DS_DIR")
    or os.environ.get("SEVENDTD_SERVER_DIR")
    or str(Path.home() / ".local/share/Steam/steamapps/common/7 Days to Die Dedicated Server")
)
ES_CFG = DS / "Mods/EfficientServer/Config/efficientserver.json"
OUT_DIR = Path(os.environ.get("VALIDATE_OUT", str(OPT_ROOT / "server" / "logs")))


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


# argv substrings, as they appear in /proc/<pid>/cmdline, of the two long-lived
# processes these harnesses start and must be able to reap after a killed run:
# the loadgen bot runner (published dotnet output path) and the dedicated
# server binary.
LOADGEN_CMDLINE_MARKER = "net8.0/7dtd-loadgen"
DEDICATED_CMDLINE_MARKER = "7DaysToDieServer.x86_64"


def kill_matching_processes(marker: str) -> int:
    """SIGKILL every process whose argv contains ``marker``; return the count.

    A /proc walk rather than `pkill -f`: no external process, no shell pattern
    to get wrong, and this process is excluded by pid instead of by the
    bracketed-glob trick pkill needed to avoid matching its own command line.
    """
    killed = 0
    me = os.getpid()
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit() or int(entry.name) == me:
            continue
        try:
            cmdline = (entry / "cmdline").read_bytes().decode("utf-8", "replace")
        except OSError:
            continue  # exited mid-walk, or another user's process
        if marker not in cmdline:
            continue
        try:
            os.kill(int(entry.name), signal.SIGKILL)
            killed += 1
        except OSError as e:
            log(f"could not kill pid {entry.name} ({marker}): {e}")
    if killed:
        log(f"killed {killed} stray process(es) matching {marker}")
    return killed


# Knobs the path-admission A/Bs toggle. The guard restores exactly these on any
# exit path (crash-safe: a backup left by a killed run is finished or
# quarantined by the NEXT run instead of blindly clobbering operator edits).
# Diagnostics.AllowBenchGod is included because the animator harness must arm
# `es benchgod on` (the console gate refuses without it) and must put the
# operator's value back afterwards.
CFG_SWAP = ConfigSwap(
    ES_CFG,
    [
        ("Pathfinding", "MaxPathEnqueuesPerTick"),
        ("Pathfinding", "DropPathWhenFarDistSq"),
        ("Diagnostics", "AllowBenchGod"),
    ],
    log=log,
)


def ensure_server_ready(timeout_s: float = 180.0) -> None:
    # Monotonic deadline: immune to NTP steps / manual clock changes mid-wait.
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        try:
            r = B.telnet(["version"], settle=1.0)
            if r and "error" not in r.lower()[:40]:
                log("telnet ready")
                return
        except Exception as e:
            log(f"  wait telnet: {e}")
        time.sleep(3)
    # Name the window so a caller skimming a report can tell "server never came
    # up" from "came up late"; per-probe failures were logged above.
    raise RuntimeError(f"telnet not ready after {timeout_s:.0f}s of probes")


def _rewrite_installed_section(section: str, updates: dict[str, object]) -> None:
    """Set ``updates`` under ``section`` in the installed config, swap-guarded.

    Shared body of every live-file knob rewrite here: snapshot through CFG_SWAP
    before the first mutation (idempotent; restore happens in main()'s finally),
    then rewrite atomically so a tool-timeout SIGKILL mid-write cannot strand
    truncated JSON for the game's config reader or the next run's guard.
    """
    if not ES_CFG.is_file():
        raise FileNotFoundError(f"missing {ES_CFG}")
    CFG_SWAP.begin()
    cfg = json.loads(ES_CFG.read_text(encoding="utf-8"))
    cfg.setdefault(section, {}).update(updates)
    write_atomic(ES_CFG, json.dumps(cfg, indent=2) + "\n")


def write_path_config(max_cap: int, drop_far: float) -> None:
    """Rewrite EfficientServer path knobs on disk."""
    _rewrite_installed_section(
        "Pathfinding",
        {"MaxPathEnqueuesPerTick": max_cap, "DropPathWhenFarDistSq": drop_far},
    )


def write_diag_config(allow_benchgod: bool) -> None:
    """Rewrite EfficientServer diagnostic knobs (bench-god allow switch).

    `es benchgod on` refuses to arm unless Diagnostics.AllowBenchGod is true in
    the installed config, so the animator harness writes it here before issuing
    the console command. The value is restored by CFG_SWAP.restore() on every
    exit path.
    """
    _rewrite_installed_section("Diagnostics", {"AllowBenchGod": allow_benchgod})


def write_report(prefix: str, report: dict) -> Path:
    """Write a run's JSON report as OUT_DIR/<prefix>_<timestamp>.json.

    UTC, not local: across a DST fall-back the local wall clock repeats an
    hour, so two runs of an A/B pair can stamp identical names and the second
    write_atomic silently destroys the first half of the evidence pair. UTC
    has no transitions, so one name maps to exactly one instant on any host.
    """
    out = OUT_DIR / f"{prefix}_{time.strftime('%Y%m%d_%H%M%S', time.gmtime())}.json"
    # Atomic for the same reason every live-file rewrite here goes through
    # write_atomic: these runs are routinely SIGKILLed by tool timeouts, and a
    # truncated report would break whatever tails or diffs it afterwards.
    write_atomic(out, json.dumps(report, indent=2) + "\n")
    log(f"report -> {out}")
    return out


def teardown_bots(bots: subprocess.Popen[bytes] | None) -> None:
    """Stop the loadgen bot cohort on every exit path.

    A cohort left running keeps loading the server (and the host) until its
    own wall clock expires (LOADGEN_TIMEOUT is up to an hour), poisoning any
    run that follows; Ctrl-C and tool-timeout kills must tear down exactly
    like a clean end. Kick server-side first so connections drop even if the
    process misses the signal, then terminate (escalating to kill) the held
    process, then sweep stragglers orphaned by earlier killed runs.
    """
    try:
        B.telnet(["kickall", "kick all"], settle=0.5)
    except Exception:
        pass
    if bots is not None:
        try:
            bots.terminate()
            try:
                bots.wait(timeout=15)
            except subprocess.TimeoutExpired:
                bots.kill()
        except Exception:
            pass
    kill_matching_processes(LOADGEN_CMDLINE_MARKER)
