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
  BM_HOLD_SAMPLE_S (12) seconds per sample window
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
import re
import sys
import time
from pathlib import Path

from es_cfg_guard import ConfigSwap
from harness_common import (
    B,
    DS,
    ES_CFG,
    OUT_DIR,
    ensure_server_ready,
    log,
    teardown_bots,
    write_report,
)

USAGE = """\
usage: measure_es_onoff.py [-h | --help]

Live whole-mod comparison, configured entirely through environment variables
(BM_PLAYERS, BM_ZOMBIES, BM_GAMESTAGE, BM_HOLD_SAMPLE_S, SKIP_SERVER_START,
ES_ARM; see the module docstring above). Takes no options besides -h/--help.\
"""

PLAYERS = int(os.environ.get("BM_PLAYERS", "32"))
ZOMBIES = int(os.environ.get("BM_ZOMBIES", "250"))
GAMESTAGE = int(os.environ.get("BM_GAMESTAGE", "250"))
SAMPLE_S = float(os.environ.get("BM_HOLD_SAMPLE_S", "35"))
SKIP_START = os.environ.get("SKIP_SERVER_START", "0") == "1"
ARM = os.environ.get("ES_ARM", "")  # "on" or "off" = matched-arm mode (fresh server per arm)

# Toggle-mode crash safety: Enabled is snapshotted before the first rewrite
# and put back on any exit path. A backup left by a killed run is finished or
# quarantined by the NEXT run (es_cfg_guard) instead of clobbering later edits.
# Matched-arm mode never snapshots: the caller owns the config between arms.
ES_SWAP = ConfigSwap(ES_CFG, [("Enabled",)], log=log)
# Where the loadgen-booted server writes its Unity log: mirror the sibling's
# own resolution exactly (start_dedicated_prefab.sh USERDATA), NOT
# SEVENDTD_LOGDIR (that names run_server.sh's output dir, which has no effect
# on a loadgen-booted server).
LOG_GLOB = Path(
    os.environ.get("RE_DEDICATED_USERDATA")
    or str(Path.home() / ".cache" / "7dtd-loadgen")
)


def latest_server_log() -> Path | None:
    # The Unity log is server_prefab_<name>__<timestamp>.txt; the stdout capture
    # is server_stdout_prefab.txt (no APM lines). Match only the Unity pattern.
    # Pick newest by mtime, not name sort: the zero-padded timestamp sorts
    # chronologically only within one world-name prefix, so logs from different
    # world names interleaved in the same dir would misorder by name.
    by_mtime = lambda paths: sorted(paths, key=lambda p: p.stat().st_mtime)
    cands = by_mtime(LOG_GLOB.glob("server_prefab_*.txt")) if LOG_GLOB.is_dir() else []
    if not cands:
        cands = by_mtime(DS.glob("logs/server_prefab_*.txt"))
    return cands[-1] if cands else None


APM_LINE_RE = re.compile(
    r"APM updates=(\d+) gmUpdateAvg=([0-9.]+)ms tickAvg=([0-9.]+)ms spikes=(\d+)"
)

# Incremental tail state per log path: byte offset, undecoded partial-line
# carry, and the newest matching APM health line seen so far. The Unity log is
# append-only and grows to hundreds of MB under blood-moon loads; rescanning
# the whole file every poll second would churn page cache and inject disk I/O
# noise into the exact frame-time numbers this harness measures.
_APM_TAIL: dict[Path, dict] = {}


def read_apm(logf: Path) -> dict | None:
    """Parse the most recent matching [7dtd-server-apm] health line from the server log.

    Reads only bytes appended since the previous call (the log is append-only;
    state resets if the file shrank or was replaced), so per-second polling
    costs one small read instead of a full-file rescan. Returns cumulative
    counters (updates, gmUpdateAvg, tickAvg, spikes); None before the first
    matching line ever.
    """
    st = _APM_TAIL.get(logf)
    try:
        size = logf.stat().st_size
    except OSError:
        return st["last"] if st else None
    if st is None or size < st["off"]:
        st = {"off": 0, "tail": b"", "last": None}
        _APM_TAIL[logf] = st
    if size > st["off"]:
        with logf.open("rb") as f:
            f.seek(st["off"])
            chunk = f.read(size - st["off"])
        st["off"] = size
        data = st["tail"] + chunk
        # Decode only complete lines; keep the unterminated tail as raw bytes
        # so a read boundary cannot split a line or a multibyte character.
        nl = data.rfind(b"\n")
        if nl >= 0:
            text = data[:nl].decode("utf-8", errors="replace")
            st["tail"] = data[nl + 1:]
            for line in text.split("\n"):
                if "[7dtd-server-apm]" not in line:
                    continue
                m = APM_LINE_RE.search(line)
                if m:
                    st["last"] = {
                        "updates": int(m.group(1)),
                        "gmUpdateAvg": float(m.group(2)),
                        "tickAvg": float(m.group(3)),
                        "spikes": int(m.group(4)),
                    }
    return st["last"]


def windowed(a: dict, b: dict) -> dict | None:
    """Windowed (instantaneous-ish) metrics from two cumulative APM reads.

    gmUpdateAvg / tickAvg are cumulative since boot; the per-window value is
    the delta of the weighted sums over the updates in between. Returns None
    when the window covers no new updates (e.g. a quiet server).
    """
    du = b["updates"] - a["updates"]
    if du <= 0:
        return None
    return {
        "gmUpdateAvg": round((b["updates"] * b["gmUpdateAvg"] - a["updates"] * a["gmUpdateAvg"]) / du, 3),
        "tickAvg": round((b["updates"] * b["tickAvg"] - a["updates"] * a["tickAvg"]) / du, 3),
        "spikes": b["spikes"],
        "window_updates": du,
    }


def sample_apm(label: str, logf: Path, seconds: float = SAMPLE_S) -> dict | None:
    """Sample the windowed APM rate over a window; None if the bridge is silent."""
    first = read_apm(logf)
    if first is None:
        return None
    # Monotonic window so a wall-clock step cannot truncate the sample period.
    t0 = time.monotonic()
    last = first
    while time.monotonic() - t0 < seconds:
        time.sleep(1.0)
        r = read_apm(logf)
        if r is not None and r["updates"] > last["updates"]:
            last = r
    w = windowed(first, last)
    if w is None:
        return None
    w["label"] = label
    return w


def set_config_enabled(on: bool, live_reload: bool) -> None:
    """Set Enabled in the installed config. With live_reload, apply it now via
    `es reload` (toggle mode); otherwise stage it for the next boot only
    (matched-arm mode: the fresh server starts with the arm's setting)."""
    cfg = json.loads(ES_CFG.read_text(encoding="utf-8"))
    cfg["Enabled"] = on
    ES_CFG.write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8")
    if not live_reload:
        log(f"ES Enabled={on} set before boot (matched-arm mode)")
        return
    B.telnet(["es reload"], settle=2.0)
    log(f"ES Enabled={on} written + es reload")


def main() -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    B.PLAYERS, B.ZOMBIES, B.GAMESTAGE = PLAYERS, ZOMBIES, GAMESTAGE
    report = {"players": PLAYERS, "zombies": ZOMBIES, "gamestage": GAMESTAGE, "phases": {}}
    bots = None
    code = 0
    try:
        # Toggle mode: snapshot Enabled before anything rewrites it. Arm mode
        # leaves the config to the caller, but still resolves any backup a
        # killed earlier run left behind so it cannot fire much later.
        if ARM in ("on", "off"):
            ES_SWAP.recover()
        else:
            ES_SWAP.begin()
        if not SKIP_START:
            if ARM in ("on", "off"):
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
        B.telnet(["gamestage", str(GAMESTAGE)], settle=1.0)

        log(f"=== spawn ~{ZOMBIES} endgame (ES {'ON' if ARM != 'off' else 'OFF'}) ===")
        spawned = B.spawn_endgame(ZOMBIES)
        report["spawned"] = spawned

        if ARM in ("on", "off"):
            # Matched-arm mode: fresh server booted with this arm's Enabled,
            # one sample, no toggle. Two runs (ES_ARM=on / ES_ARM=off) give the
            # canonical fresh-server-per-arm comparison.
            report["arm"] = ARM
            arm_on = ARM == "on"
            # confirm the booted setting stuck
            st = B.telnet(["es status"], settle=1.5)
            report["es_status"] = st[-400:]
            s = sample_apm("arm_" + ARM, logf)
            report["phases"]["arm_" + ARM] = s
            report["verdict"] = "arm_" + ARM
            log(f"  arm {ARM}: {s}")
            # caller owns the next arm's config
        else:
            # Single-session toggle mode: same load, ES on then off. The
            # pre-run Enabled value goes back in main()'s finally.
            set_config_enabled(True, live_reload=True)
            on = sample_apm("es_on", logf)
            report["phases"]["es_on"] = on
            log(f"  ES ON : {on}")

            set_config_enabled(False, live_reload=True)
            off = sample_apm("es_off", logf)
            report["phases"]["es_off"] = off
            log(f"  ES OFF: {off}")

            if on and off:
                d = on["gmUpdateAvg"] - off["gmUpdateAvg"]
                verdict = "ON_faster" if d < -0.5 else ("ON_slower" if d > 0.5 else "within_noise")
                report["gmUpdateAvg_delta_ms"] = round(d, 3)
                report["verdict"] = verdict
                log(f"  delta gmUpdateAvg (ON-OFF) = {d:+.3f} ms -> {verdict}")
            else:
                report["verdict"] = "no_apm_data"
                log("WARN: APM bridge silent; no numeric verdict (check 7dtd-server-apm-bridge installed)")
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
        toggle_mode = ARM not in ("on", "off")
        ES_SWAP.restore()
        if toggle_mode:
            # Best effort only: the sampled server may already be gone.
            try:
                B.telnet(["es reload"], settle=1.0)
            except Exception:
                pass
            report["restored"] = True
        teardown_bots(bots)
        write_report("es_onoff", report)
    return code


if __name__ == "__main__":
    argv = sys.argv[1:]
    if argv in (["-h"], ["--help"]):
        print(USAGE)
        raise SystemExit(0)
    if argv:
        print(f"measure_es_onoff.py: unrecognized arguments: {' '.join(argv)}", file=sys.stderr)
        print(USAGE, file=sys.stderr)
        raise SystemExit(2)
    sys.exit(main())
