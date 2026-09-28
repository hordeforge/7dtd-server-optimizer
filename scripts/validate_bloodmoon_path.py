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
import time
from typing import TypedDict

from cli_common import preflight_usage, run_cli

NAME = "scripts/validate_bloodmoon_path.py"
USAGE = """\
usage: scripts/validate_bloodmoon_path.py [-h | --help]

Live blood-moon path-admission A/B, configured entirely through environment
variables. Takes no options besides -h/--help.

Environment:
  SEVENDTD_DS_DIR         dedicated install root (SEVENDTD_SERVER_DIR is also
                          accepted; default: the stock Steam path)
  BM_PLAYERS              bots to join (default 12)
  BM_GAMESTAGE            game stage (default 250)
  BM_HOLD_SAMPLE_S        seconds per sample window (default 12)
  PATH_CAP                MaxPathEnqueuesPerTick under test (default 64)
  PATH_DROP_FAR_SQ        path-drop distance squared (default 2500)
  SKIP_SERVER_START       1 to drive a server that is already running\
"""

if __name__ == "__main__":
    # Ahead of the harness_common import below, which puts the sibling
    # 7dtd-loadgen tree on sys.path: reading this usage must not depend on
    # that tree being cloned next to the repo.
    preflight_usage(NAME, USAGE)

from harness_common import (
    CFG_SWAP,
    OUT_DIR,
    B,
    ensure_server_ready,
    join_cohort,
    log,
    restore_and_reload,
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


class HealthSample(TypedDict):
    """One averaged health window, as embedded in the run report."""

    label: str
    frameMs_avg: float | None
    frameMs_max: float | None
    tickAvgMs_avg: float | None
    entityAlives: int
    players: object
    # Health polls the window actually took; each series below may be thinner
    # when its field came back missing, so this is the depth of the window, not
    # the count behind either average.
    samples: int


def sample_health(label: str, seconds: float = SAMPLE_S) -> HealthSample:
    frames, ticks = [], []
    polls = 0
    # Monotonic window so a wall-clock step cannot truncate the sample period.
    t0 = time.monotonic()
    while time.monotonic() - t0 < seconds:
        h = B.health()
        polls += 1
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
        # The polls the window actually took, not max(len(frames), len(ticks)):
        # the two filters are independent, so the max overstates the depth of
        # whichever series was thinner, and an operator reads `samples` to judge
        # whether a window was deep enough to trust.
        "samples": polls,
    }


def cluster_players() -> list[int]:
    """Teleport all joined bots onto the first bot so they form ONE blood-moon
    party (party join is within 80 m, RE aidirector.md AddPlayerToParty).
    Scattered bots each make a Party of 1 -> enemy max 2 -> tiny horde."""
    # Pin the count type at the loadgen boundary: player_ids() is untyped in
    # the sibling tree, and every consumer below assumes bot entity ids.
    ids: list[int] = B.player_ids()
    if len(ids) < 2:
        return ids
    anchor = ids[0]
    # One connection for the whole cohort: each telnet() call pays TCP setup +
    # password + drain windows, so per-bot connections serialize into ~1.5 s
    # of pure overhead per bot (same batching fast_spawn uses).
    B.telnet([f"teleportplayer {pid} {anchor}" for pid in ids[1:]], settle=1.0)
    log(f"clustered {len(ids)} bots onto {anchor}")
    time.sleep(5)
    return ids


def main() -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    B.PLAYERS, B.GAMESTAGE = PLAYERS, GAMESTAGE
    # Nested containers are built here and embedded by reference so the
    # verdict/phase writes below index a precisely typed dict instead of
    # reaching through report's object-valued slots.
    phases: dict[str, object] = {}
    verdicts: dict[str, str] = {}
    report = {
        "players": PLAYERS,
        "gamestage": GAMESTAGE,
        "path_cap": PATH_CAP,
        "path_drop_far_sq": PATH_DROP,
        "mode": "bloodmoon",
        "phases": phases,
        "verdicts": verdicts,
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

        bots, joined, join_verdict = join_cohort(PLAYERS)
        report["joined"] = joined
        verdicts["join"] = join_verdict
        if join_verdict != "PASS":
            return 2
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
        phases["path_baseline"] = base
        log(f"  path baseline: {base}")

        write_path_config(PATH_CAP, PATH_DROP)
        B.telnet(["es reload"], settle=1.5)
        on = sample_health("path_admission_on")
        phases["path_admission_on"] = on
        log(f"  path on ({PATH_CAP}/{PATH_DROP}): {on}")

        fb, fo = base.get("frameMs_avg"), on.get("frameMs_avg")
        if isinstance(fb, (int, float)) and isinstance(fo, (int, float)):
            delta = fo - fb
            report["frame_delta_ms"] = round(delta, 2)
            # Higher alive on the 'on' phase = load imbalance, not a regression.
            da = (on.get("entityAlives") or 0) - (base.get("entityAlives") or 0)
            report["alive_delta"] = da
            verdicts["path_frame"] = (
                "PASS" if delta < -2 else ("noise_or_load_imbalance" if da > 0 else "no_win")
            )
        else:
            verdicts["path_frame"] = "no_health_data"
        log(f"=== VERDICTS: {json.dumps(verdicts)} ===")
    except KeyboardInterrupt:
        return 130
    except Exception as e:
        # Same contract as the sibling harnesses: record the failure in the
        # report instead of dying on a bare traceback; the finally below still
        # restores the config and writes the report.
        log(f"FAIL exception: {e}")
        report["error"] = repr(e)
        verdicts["overall"] = "ERROR"
        return 4
    finally:
        # Isolated so a restore failure cannot skip bot teardown (a leaked
        # cohort keeps loading the server) or the report write.
        restore_and_reload(CFG_SWAP)
        teardown_bots(bots)
        write_report("bloodmoon_path", report)
    return 0


if __name__ == "__main__":
    run_cli(NAME, USAGE, main)
