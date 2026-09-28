#!/usr/bin/env python3
"""Whole-mod EfficientServer on/off APM comparison (live).

Boots the stock loadgen dedicated server (Navezgane), joins bots, spawns
endgame zombies, then samples server health twice under the same load:

  Phase ON : EfficientServer Enabled=true  (config as installed)
  Phase OFF: Enabled=false via config rewrite + `es reload`

Samples are read from the server log's periodic `[7dtd-server-apm]` lines
(gmUpdateAvg / tickAvg) plus telnet health, so the comparison is
APM-bridge ground truth, not a frame-timer. Restores the config and
writes a JSON report under server/logs/.

Env (subset of bloodmoon_profile):
  BM_PLAYERS (default 32), BM_ZOMBIES (default 250), BM_GAMESTAGE (250)
  BM_HOLD_SAMPLE_S (35) seconds per sample window
  SKIP_SERVER_START=1 if a dedicated server is already running
  SEVENDTD_TELNET_PASSWORD (default retest)

Exit 0 on a completed comparison; the verdict field records whether ES ON
was faster, slower, or within noise on gmUpdateAvg under the same load.

Matched-arm mode (the canonical comparison): set ES_ARM=on or ES_ARM=off.
The fresh server boots with that arm's Enabled value (config written before
start), one sample is taken, and no toggle happens. Run twice (once per
arm) for a fresh-server-per-arm comparison; the caller owns the config
between arms (set it explicitly, or reinstall to restore the default).
"""
from __future__ import annotations

import json
import os
import time
from collections.abc import Iterable
from pathlib import Path

from bench_parse import ApmSample, read_apm, windowed
from cli_common import preflight_usage, run_cli
from es_cfg_guard import CFG_ENCODING, ConfigSwap, write_atomic

NAME = "scripts/measure_es_onoff.py"
USAGE = """\
usage: scripts/measure_es_onoff.py [-h | --help]

Live whole-mod comparison, configured entirely through environment variables.
Takes no options besides -h/--help. Exits 0 on a completed comparison; the
report's verdict field records whether ES ON was faster, slower or within noise
on gmUpdateAvg under the same load.

Environment:
  SEVENDTD_DS_DIR         dedicated install root (SEVENDTD_SERVER_DIR is also
                          accepted; default: the stock Steam path)
  BM_PLAYERS              bots to join (default 32)
  BM_ZOMBIES              endgame zombies spawned (default 250)
  BM_GAMESTAGE            game stage (default 250)
  BM_HOLD_SAMPLE_S        seconds per sample window (default 35)
  SKIP_SERVER_START       1 to measure a server that is already running
  ES_ARM                  on|off for matched-arm mode: boot fresh with that
                          arm's Enabled value, sample once, toggle nothing.
                          Run once per arm for a fresh-server-per-arm pair
  SEVENDTD_TELNET_PASSWORD  telnet password (default retest)
  RE_DEDICATED_USERDATA   loadgen userdata dir holding the Unity server log
                          (default ~/.cache/7dtd-loadgen)
  VALIDATE_OUT            report output dir (default server/logs)\
"""

if __name__ == "__main__":
    # Ahead of the harness_common import below, which puts the sibling
    # 7dtd-loadgen tree on sys.path: reading this usage must not depend on
    # that tree being cloned next to the repo.
    preflight_usage(NAME, USAGE)

from harness_common import (
    DS,
    ES_CFG,
    OUT_DIR,
    B,
    ensure_server_ready,
    log,
    teardown_bots,
    write_report,
)

PLAYERS = int(os.environ.get("BM_PLAYERS", "32"))
ZOMBIES = int(os.environ.get("BM_ZOMBIES", "250"))
GAMESTAGE = int(os.environ.get("BM_GAMESTAGE", "250"))
SAMPLE_S = float(os.environ.get("BM_HOLD_SAMPLE_S", "35"))
SKIP_START = os.environ.get("SKIP_SERVER_START", "0") == "1"
ARM = os.environ.get("ES_ARM", "")  # "on" or "off" = matched-arm mode (fresh server per arm)
# One spelling of "matched-arm mode is active"; ARM is consulted for which arm,
# this for whether arm mode applies at all.
ARM_MODE = ARM in ("on", "off")

# Toggle-mode crash safety: Enabled is snapshotted before the first rewrite
# and put back on any exit path. A backup left by a killed run is finished or
# quarantined by the NEXT run (es_cfg_guard) instead of clobbering later edits.
# Matched-arm mode never snapshots: the caller owns the config between arms.
ES_SWAP = ConfigSwap(ES_CFG, [("Enabled",)], log=log)
# Directory the loadgen-booted server writes its Unity log into: mirror the
# sibling's own resolution exactly (start_dedicated_prefab.sh USERDATA), NOT
# SEVENDTD_LOGDIR (that names run_server.sh's output dir, which has no effect
# on a loadgen-booted server).
LOG_DIR = Path(
    os.environ.get("RE_DEDICATED_USERDATA")
    or str(Path.home() / ".cache" / "7dtd-loadgen")
)


def latest_server_log() -> Path | None:
    # The Unity log is server_prefab_<name>__<timestamp>.txt; the stdout capture
    # is server_stdout_prefab.txt (no APM lines). Match only the Unity pattern.
    # Pick newest by mtime, not name sort: the zero-padded timestamp sorts
    # chronologically only within one world-name prefix, so logs from different
    # world names interleaved in the same dir would misorder by name.
    def by_mtime(paths: Iterable[Path]) -> list[Path]:
        # st_mtime_ns, not st_mtime: the float seconds lose resolution on a
        # coarse-mtime filesystem, and a tie is not a rare case here because
        # the server writes its next log as soon as the previous run exits.
        # sorted() is stable, so an exact tie would fall back to glob order
        # and silently hand back a stale log to sample - an A/B verdict then
        # measures the wrong run. Name breaks the tie deterministically.
        return sorted(paths, key=lambda p: (p.stat().st_mtime_ns, p.name))

    cands = by_mtime(LOG_DIR.glob("server_prefab_*.txt")) if LOG_DIR.is_dir() else []
    if not cands:
        cands = by_mtime(DS.glob("logs/server_prefab_*.txt"))
    return cands[-1] if cands else None




# The APM line parser, the incremental reader and the windowed-rate
# reconstruction live in bench_parse: they parse the server log, which is text
# this repo does not write, and keeping them here put them behind the
# 7dtd-loadgen sibling import where no gate could reach them. The fuzz target
# for both is `python3 scripts/bench_parse.py --selftest`.


def sample_apm(label: str, logf: Path, seconds: float = SAMPLE_S) -> ApmSample | None:
    """Sample the windowed APM rate over a window; None if the bridge is silent."""
    first = read_apm(logf, warn=log)
    if first is None:
        return None
    # Monotonic window so a wall-clock step cannot truncate the sample period.
    t0 = time.monotonic()
    last = first
    while time.monotonic() - t0 < seconds:
        time.sleep(1.0)
        r = read_apm(logf, warn=log)
        if r is not None and r["updates"] > last["updates"]:
            last = r
    win = windowed(first, last)
    if win is None:
        return None
    return ApmSample(label=label, **win)


def set_config_enabled(on: bool, live_reload: bool) -> None:
    """Set Enabled in the installed config. With live_reload, apply it now via
    `es reload` (toggle mode); otherwise stage it for the next boot only
    (matched-arm mode: the fresh server starts with the arm's setting)."""
    cfg = json.loads(ES_CFG.read_text(encoding=CFG_ENCODING))
    cfg["Enabled"] = on
    # Atomic: same kill-mid-write hazard write_path_config guards against.
    write_atomic(ES_CFG, json.dumps(cfg, indent=2) + "\n")
    if not live_reload:
        log(f"ES Enabled={on} set before boot (matched-arm mode)")
        return
    B.telnet(["es reload"], settle=2.0)
    log(f"ES Enabled={on} written + es reload")


def main() -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    B.PLAYERS, B.ZOMBIES, B.GAMESTAGE = PLAYERS, ZOMBIES, GAMESTAGE
    # Embedded by reference so the phase writes below index a precisely typed
    # dict instead of reaching through report's object-valued slots. Values are
    # nullable on purpose: sample_apm returns None when no APM window was read,
    # and the report must record that absence rather than drop the phase.
    phases: dict[str, ApmSample | None] = {}
    report = {"players": PLAYERS, "zombies": ZOMBIES, "gamestage": GAMESTAGE, "phases": phases}
    bots = None
    code = 0
    try:
        # Toggle mode: snapshot Enabled before anything rewrites it. Arm mode
        # leaves the config to the caller, but still resolves any backup a
        # killed earlier run left behind so it cannot fire much later.
        if ARM_MODE:
            ES_SWAP.recover()
        else:
            ES_SWAP.begin()
        if not SKIP_START:
            if ARM_MODE:
                set_config_enabled(ARM == "on", live_reload=False)
            B.start_server()
        ensure_server_ready()
        logf = latest_server_log()
        if logf is None:
            log("FAIL: no server log found for APM reads")
            return 2
        report["server_log"] = str(logf)

        bots, joined = B.join_ramped(PLAYERS)
        report["joined"] = joined
        if joined < max(1, int(PLAYERS * 0.5)):
            log(f"FAIL: only {joined}/{PLAYERS} joined")
            return 2
        # Same helper as the sibling harnesses: there is no `gamestage` console
        # command (stage derives from player XP), so set_gamestage grants XP.
        B.set_gamestage(GAMESTAGE)

        log(f"=== spawn ~{ZOMBIES} endgame (ES {'ON' if ARM != 'off' else 'OFF'}) ===")
        spawned = B.spawn_endgame(ZOMBIES)
        report["spawned"] = spawned

        if ARM_MODE:
            # Matched-arm mode: fresh server booted with this arm's Enabled,
            # one sample, no toggle. Two runs (ES_ARM=on / ES_ARM=off) give the
            # canonical fresh-server-per-arm comparison.
            report["arm"] = ARM
            # confirm the booted setting stuck
            st = B.telnet(["es status"], settle=1.5)
            report["es_status"] = st[-400:]
            s = sample_apm("arm_" + ARM, logf)
            phases["arm_" + ARM] = s
            report["verdict"] = "arm_" + ARM
            log(f"  arm {ARM}: {s}")
            # caller owns the next arm's config
        else:
            # Single-session toggle mode: same load, ES on then off. The
            # pre-run Enabled value goes back in main()'s finally.
            set_config_enabled(True, live_reload=True)
            on = sample_apm("es_on", logf)
            phases["es_on"] = on
            log(f"  ES ON : {on}")

            set_config_enabled(False, live_reload=True)
            off = sample_apm("es_off", logf)
            phases["es_off"] = off
            log(f"  ES OFF: {off}")

            if on and off:
                d = on["gmUpdateAvg"] - off["gmUpdateAvg"]
                # The reconstruction error bounds (see windowed) set the floor
                # for "same": on a long-uptime server they exceed the 0.5 ms
                # heuristic, and a delta inside them is print-rounding noise,
                # not an ES effect.
                noise = max(0.5, on["gmUpdateAvg_err_ms"] + off["gmUpdateAvg_err_ms"])
                verdict = (
                    "within_noise"
                    if abs(d) <= noise
                    else ("ON_faster" if d < 0 else "ON_slower")
                )
                report["gmUpdateAvg_delta_ms"] = round(d, 3)
                report["gmUpdateAvg_noise_bound_ms"] = round(noise, 3)
                report["verdict"] = verdict
                log(
                    f"  delta gmUpdateAvg (ON-OFF) = {d:+.3f} ms"
                    f" (noise bound {noise:.3f} ms) -> {verdict}"
                )
            else:
                report["verdict"] = "no_apm_data"
                log(
                    "WARN: APM bridge silent; no numeric verdict"
                    " (check 7dtd-server-apm-bridge installed)"
                )
    except KeyboardInterrupt:
        code = 130
    except Exception as e:
        # Same contract as the sibling validate_* harnesses: record the failure
        # in the report instead of dying on a bare traceback; the finally below
        # still restores the config and writes the report.
        log(f"FAIL exception: {e}")
        report["error"] = repr(e)
        report["verdict"] = "ERROR"
        code = 4
    finally:
        toggle_mode = not ARM_MODE
        # Each cleanup step is isolated so one failure cannot skip the rest:
        # a restore error must not leak the bot cohort (it keeps loading the
        # server until its own wall clock expires) or lose the report.
        restored = True
        try:
            ES_SWAP.restore()
        except Exception as e:
            log(f"WARN: config restore failed ({e}); backup kept for next run")
            restored = False
        if toggle_mode:
            # Best effort only: the sampled server may already be gone. Skipped
            # when the restore failed, so a reload cannot re-apply the harness
            # values the restore just failed to revert; the report must say
            # "restored": false then, not claim success.
            reloaded = True
            if restored:
                try:
                    B.telnet(["es reload"], settle=1.0)
                except Exception as e:
                    # Never silent: a failed reload leaves this server running the
                    # harness's Enabled value even though the file on disk is the
                    # operator's again, which is exactly the state the run is
                    # supposed to end without.
                    log(f"  es reload after restore failed ({e}); the running server "
                        "still has this run's Enabled value until someone reloads it")
                    reloaded = False
            report["restored"] = restored
            report["reloaded"] = reloaded
        teardown_bots(bots)
        write_report("es_onoff", report)
    return code


if __name__ == "__main__":
    run_cli(NAME, USAGE, main)
