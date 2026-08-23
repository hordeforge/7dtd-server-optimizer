#!/usr/bin/env python3
"""Blood-moon path-admission profile (live, genuine director-spawned horde).

Brings up the loadgen dedicated server, joins a stable bot cohort, triggers
a REAL blood moon (settime <day> 22 0 + setgamestat BloodMoonDay <day>), and
lets the AIDirectorBloodMoonComponent spawn the party-scaled horde through
its normal path. Then runs the path-admission A/B under that horde:

  baseline:  path knobs off (vanilla)
  path on:   MaxPathEnqueuesPerTick=cap + DropPathWhenFarDistSq=dropFarSq

Sample windows read server health via telnet `apm dump` (frame + tick), the
same metric the animator/path harness uses. Writes a JSON report.

Env:
  BM_PLAYERS (default 12 - the LiteNetLib join flake blocks >12 stable bots)
  PATH_CAP (64), PATH_DROP_FAR_SQ (2500)
  BM_HOLD_SAMPLE_S (12) per window
  SKIP_SERVER_START=1 if a dedicated server is already running
"""
from __future__ import annotations

import json
import os
import sys
import time

from harness_common import (
    B,
    CFG_SWAP,
    OUT_DIR,
    ensure_server_ready,
    log,
    teardown_bots,
    write_path_config,
    write_report,
)

PLAYERS = int(os.environ.get("BM_PLAYERS", "12"))
GAMESTAGE = int(os.environ.get("BM_GAMESTAGE", "250"))
PATH_CAP = int(os.environ.get("PATH_CAP", "64"))
PATH_DROP = float(os.environ.get("PATH_DROP_FAR_SQ", "2500"))
SAMPLE_S = float(os.environ.get("BM_HOLD_SAMPLE_S", "12"))
SKIP_START = os.environ.get("SKIP_SERVER_START", "0") == "1"


def sample_health(label: str, seconds: float = SAMPLE_S) -> dict:
    frames, ticks = [], []
    # Monotonic window so a wall-clock step cannot truncate the sample period.
    t0 = time.monotonic()
    while time.monotonic() - t0 < seconds:
        h = B.health()
        if h.get("frameMs") is not None:
            frames.append(float(h["frameMs"]))
        if h.get("tickAvgMs") is not None:
            ticks.append(float(h["tickAvgMs"]))
        time.sleep(1.0)
    return {
        "label": label,
        "frameMs_avg": round(sum(frames) / len(frames), 2) if frames else None,
        "frameMs_max": round(max(frames), 2) if frames else None,
        "tickAvgMs_avg": round(sum(ticks) / len(ticks), 3) if ticks else None,
        "entityAlives": B.alive(),
        "players": B.snap_players(),
        "samples": max(len(frames), len(ticks)),
    }


def cluster_players() -> list[int]:
    """Teleport all joined bots onto the first bot so they form ONE blood-moon
    party (party join is within 80 m, RE aidirector.md AddPlayerToParty).
    Scattered bots each make a Party of 1 -> enemy max 2 -> tiny horde."""
    ids = B.player_ids()
    if len(ids) < 2:
        return ids
    anchor = ids[0]
    for pid in ids[1:]:
        B.telnet([f"teleportplayer {pid} {anchor}"], settle=0.4)
    log(f"clustered {len(ids)} bots onto {anchor}")
    time.sleep(5)
    return ids


def main() -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    B.PLAYERS, B.GAMESTAGE = PLAYERS, GAMESTAGE
    report = {
        "players": PLAYERS,
        "gamestage": GAMESTAGE,
        "path_cap": PATH_CAP,
        "path_drop_far_sq": PATH_DROP,
        "mode": "bloodmoon",
        "phases": {},
        "verdicts": {},
    }
    bots = None
    try:
        # Snapshot before anything can mutate the installed config; on any
        # exit path (including a kill mid-run, recovered by the next run)
        # only these harness-owned knobs are reverted.
        CFG_SWAP.begin()
        if not SKIP_START:
            B.start_server()
        ensure_server_ready()

        bots, joined = B.join_ramped(PLAYERS)
        report["joined"] = joined
        if joined < max(1, int(PLAYERS * 0.5)):
            log(f"FAIL: only {joined}/{PLAYERS} joined")
            report["verdicts"]["join"] = "FAIL"
            return 2
        report["verdicts"]["join"] = "PASS"
        B.set_gamestage(GAMESTAGE)

        # Cluster bots into one party so the blood-moon horde scales (party GS +
        # member count drive the spawn budget).
        cluster_players()

        log("=== trigger genuine blood moon ===")
        bm_day = B.spawn_bloodmoon()
        report["bloodmoon_day"] = bm_day
        time.sleep(20)  # let the director's first waves start
        alive0 = B.alive()
        log(f"blood-moon horde building: alive={alive0}")
        time.sleep(30)  # let the horde ramp toward a siege

        # Path-admission A/B under the blood-moon horde.
        write_path_config(0, 0.0)
        B.telnet(["es reload"], settle=1.5)
        base = sample_health("path_baseline")
        report["phases"]["path_baseline"] = base
        log(f"  path baseline: {base}")

        write_path_config(PATH_CAP, PATH_DROP)
        B.telnet(["es reload"], settle=1.5)
        on = sample_health("path_admission_on")
        report["phases"]["path_admission_on"] = on
        log(f"  path on ({PATH_CAP}/{PATH_DROP}): {on}")

        fb, fo = base.get("frameMs_avg"), on.get("frameMs_avg")
        if isinstance(fb, (int, float)) and isinstance(fo, (int, float)):
            delta = fo - fb
            report["frame_delta_ms"] = round(delta, 2)
            # Higher alive on the 'on' phase = load imbalance, not a regression.
            da = (on.get("entityAlives") or 0) - (base.get("entityAlives") or 0)
            report["alive_delta"] = da
            report["verdicts"]["path_frame"] = (
                "PASS" if delta < -2 else ("noise_or_load_imbalance" if da > 0 else "no_win")
            )
        else:
            report["verdicts"]["path_frame"] = "no_health_data"
        log(f"=== VERDICTS: {json.dumps(report['verdicts'])} ===")
    except KeyboardInterrupt:
        return 130
    except Exception as e:
        # Same contract as the sibling harnesses: record the failure in the
        # report instead of dying on a bare traceback; the finally below still
        # restores the config and writes the report.
        log(f"FAIL exception: {e}")
        report["error"] = repr(e)
        report["verdicts"]["overall"] = "ERROR"
        return 4
    finally:
        CFG_SWAP.restore()
        teardown_bots(bots)
        write_report("bloodmoon_path", report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
