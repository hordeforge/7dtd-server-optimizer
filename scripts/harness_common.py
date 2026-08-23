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
import subprocess
import sys
import time
from pathlib import Path

OPT_ROOT = Path(__file__).resolve().parent.parent
LOADGEN_ROOT = OPT_ROOT.parent / "7dtd-loadgen"
sys.path.insert(0, str(LOADGEN_ROOT / "scripts"))

import bloodmoon_profile as B  # noqa: E402
from es_cfg_guard import ConfigSwap  # noqa: E402

DS = Path(
    os.environ.get(
        "SEVENDTD_SERVER_DIR",
        str(Path.home() / ".local/share/Steam/steamapps/common/7 Days to Die Dedicated Server"),
    )
)
ES_CFG = DS / "Mods/EfficientServer/Config/efficientserver.json"
OUT_DIR = Path(os.environ.get("VALIDATE_OUT", str(OPT_ROOT / "server" / "logs")))


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


# Knobs the path-admission A/Bs toggle. The guard restores exactly these on any
# exit path (crash-safe: a backup left by a killed run is finished or
# quarantined by the NEXT run instead of blindly clobbering operator edits).
CFG_SWAP = ConfigSwap(
    ES_CFG,
    [
        ("Pathfinding", "MaxPathEnqueuesPerTick"),
        ("Pathfinding", "DropPathWhenFarDistSq"),
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
    raise RuntimeError("telnet not ready")


def write_path_config(max_cap: int, drop_far: float) -> None:
    """Rewrite EfficientServer path knobs on disk."""
    if not ES_CFG.is_file():
        raise FileNotFoundError(f"missing {ES_CFG}")
    # Snapshot once per run (idempotent); restore happens in main()'s finally.
    CFG_SWAP.begin()
    cfg = json.loads(ES_CFG.read_text(encoding="utf-8"))
    pf = cfg.setdefault("Pathfinding", {})
    pf["MaxPathEnqueuesPerTick"] = max_cap
    pf["DropPathWhenFarDistSq"] = drop_far
    ES_CFG.write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8")


def write_report(prefix: str, report: dict) -> Path:
    """Write a run's JSON report as OUT_DIR/<prefix>_<timestamp>.json."""
    out = OUT_DIR / f"{prefix}_{time.strftime('%Y%m%d_%H%M%S')}.json"
    out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    log(f"report -> {out}")
    return out


def teardown_bots(bots) -> None:
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
    # Bracketed [n] so the pattern cannot match its own pkill command line.
    subprocess.run(["pkill", "-9", "-f", "net8.0/7dtd-loadge[n]"], check=False)
