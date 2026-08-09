#!/usr/bin/env python3
"""Whole-mod EfficientServer on/off APM comparison (live).

Boots the stock loadgen dedicated server (Navezgane), joins bots, spawns
endgame zombies, then samples server health twice under the same load:

  Phase ON : EfficientServer Enabled=true  (config as installed)
  Phase OFF: Enabled=false via config rewrite + `es reload`

Samples are read from the server log's periodic `[7dtd-apm]` lines
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
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

OPT_ROOT = Path(__file__).resolve().parent.parent
LOADGEN_ROOT = OPT_ROOT.parent / "7dtd-loadgen"
sys.path.insert(0, str(LOADGEN_ROOT / "scripts"))
import bloodmoon_profile as B  # noqa: E402

PLAYERS = int(os.environ.get("BM_PLAYERS", "32"))
ZOMBIES = int(os.environ.get("BM_ZOMBIES", "250"))
GAMESTAGE = int(os.environ.get("BM_GAMESTAGE", "250"))
SAMPLE_S = float(os.environ.get("BM_HOLD_SAMPLE_S", "35"))
SKIP_START = os.environ.get("SKIP_SERVER_START", "0") == "1"
OUT_DIR = Path(os.environ.get("VALIDATE_OUT", str(OPT_ROOT / "server" / "logs")))
DS = Path(
    os.environ.get(
        "SEVENDTD_SERVER_DIR",
        str(Path.home() / ".local/share/Steam/steamapps/common/7 Days to Die Dedicated Server"),
    )
)
ES_CFG = DS / "Mods/EfficientServer/Config/efficientserver.json"
ES_CFG_BAK = ES_CFG.with_suffix(".json.esab-bak")
LOG_GLOB = Path(os.environ.get("SEVENDTD_LOGDIR", str(Path.home() / ".cache" / "7dtd-loadgen")))


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


def ensure_server_ready(timeout_s: float = 180.0) -> None:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            r = B.telnet(["version"], settle=1.0)
            if r and "error" not in r.lower()[:40]:
                log("telnet ready")
                return
        except Exception as e:
            log(f"  wait telnet: {e}")
        time.sleep(3)
    raise RuntimeError("telnet not ready")


def latest_server_log() -> Path | None:
    # The Unity log is server_prefab_<name>__<timestamp>.txt; the stdout capture
    # is server_stdout_prefab.txt (no APM lines). Match only the Unity pattern.
    cands = sorted(LOG_GLOB.glob("server_prefab_*.txt")) if LOG_GLOB.is_dir() else []
    if not cands:
        cands = sorted(DS.glob("logs/server_prefab_*.txt"))
    return cands[-1] if cands else None


def read_apm(logf: Path) -> dict | None:
    """Parse the most recent matching [7dtd-apm] health line from the server log.

    Scans backwards: APM health lines append every ~30 s and other [7dtd-apm]
    lines (session headers) may interleave, so the last *matching* line wins.
    Returns cumulative counters (updates, gmUpdateAvg, tickAvg, spikes).
    """
    txt = logf.read_text(encoding="utf-8", errors="replace")
    m = None
    for line in reversed(txt.splitlines()):
        if "[7dtd-apm]" not in line:
            continue
        m = re.search(
            r"APM updates=(\d+) gmUpdateAvg=([0-9.]+)ms tickAvg=([0-9.]+)ms spikes=(\d+)", line
        )
        if m:
            break
    if not m:
        return None
    return {
        "updates": int(m.group(1)),
        "gmUpdateAvg": float(m.group(2)),
        "tickAvg": float(m.group(3)),
        "spikes": int(m.group(4)),
    }


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
    t0 = time.time()
    last = first
    while time.time() - t0 < seconds:
        time.sleep(1.0)
        r = read_apm(logf)
        if r is not None and r["updates"] > last["updates"]:
            last = r
    w = windowed(first, last)
    if w is None:
        return None
    w["label"] = label
    return w


def set_enabled(on: bool) -> None:
    cfg = json.loads(ES_CFG.read_text(encoding="utf-8"))
    cfg["Enabled"] = on
    ES_CFG.write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8")
    B.telnet(["es reload"], settle=2.0)
    log(f"ES Enabled={on} written + es reload")


def main() -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    B.PLAYERS, B.ZOMBIES, B.GAMESTAGE = PLAYERS, ZOMBIES, GAMESTAGE
    report = {"players": PLAYERS, "zombies": ZOMBIES, "gamestage": GAMESTAGE, "phases": {}}
    code = 0
    try:
        if not SKIP_START:
            os.environ["BM_PLAYERS"] = str(PLAYERS)
            os.environ["BM_ZOMBIES"] = str(ZOMBIES)
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

        log(f"=== spawn ~{ZOMBIES} endgame (ES ON) ===")
        spawned = B.spawn_endgame(ZOMBIES)
        report["spawned"] = spawned

        # Phase ON (as installed: Enabled=true)
        set_enabled(True)
        on = sample_apm("es_on", logf)
        report["phases"]["es_on"] = on
        log(f"  ES ON : {on}")

        # Phase OFF (Enabled=false + reload), same load
        set_enabled(False)
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
            log("WARN: APM bridge silent; no numeric verdict (check 7dtd-apm-bridge installed)")

        # restore
        set_enabled(True)
        report["restored"] = True
    except KeyboardInterrupt:
        code = 130
    finally:
        if ES_CFG_BAK.is_file():
            shutil.copy2(ES_CFG_BAK, ES_CFG)
            ES_CFG_BAK.unlink(missing_ok=True)
        out = OUT_DIR / f"es_onoff_{time.strftime('%Y%m%d_%H%M%S')}.json"
        out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        log(f"report -> {out}")
    return code


if __name__ == "__main__":
    sys.exit(main())
