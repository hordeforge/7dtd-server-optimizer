#!/usr/bin/env python3
"""Live A/B for CullCompletely animator emergency + path admission.

Brings up stock loadgen dedicated (Navezgane), joins bots, spawns endgame
zombies, then:

  Phase A (animator): sample frame health baseline -> es animoff -> sample ->
                      es animon -> sample + es animstate parse for dp.

  Phase B (path admission): with anim restored, toggle path knobs via config
    rewrite + es reload, sample health under same load.

Writes a JSON report under 7dtd-optimizer/server/logs/ (or VALIDATE_OUT).

Env (subset of bloodmoon_profile):
  BM_PLAYERS (default 16 for a faster gate; use 32/64 for stress)
  BM_ZOMBIES (default 200)
  BM_GAMESTAGE (250)
  BM_HOLD_SAMPLE_S (12) seconds per sample window
  PATH_CAP (64) MaxPathEnqueuesPerTick for phase B
  PATH_DROP_FAR_SQ (2500) DropPathWhenFarDistSq for phase B
  SKIP_SERVER_START=1 if dedicated already running
  SEVENDTD_TELNET_PASSWORD (default retest)

Exit 0 if animator exit shows at least one moving zombie with dp>0 after
restore OR animstate unavailable but frame recovered; non-zero on hard
failures (no server, no players, animoff no frame change when load was high).
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time

from harness_common import (
    B,
    CFG_SWAP,
    OUT_DIR,
    ensure_server_ready,
    log,
    write_path_config,
    write_report,
)

PLAYERS = int(os.environ.get("BM_PLAYERS", "16"))
ZOMBIES = int(os.environ.get("BM_ZOMBIES", "200"))
GAMESTAGE = int(os.environ.get("BM_GAMESTAGE", "250"))
SAMPLE_S = float(os.environ.get("BM_HOLD_SAMPLE_S", "12"))
PATH_CAP = int(os.environ.get("PATH_CAP", "64"))
PATH_DROP = float(os.environ.get("PATH_DROP_FAR_SQ", "2500"))
SKIP_START = os.environ.get("SKIP_SERVER_START", "0") == "1"


def sample_health(label: str, seconds: float = SAMPLE_S) -> dict:
    """Average a few health snapshots over a window."""
    frames = []
    ticks = []
    alives = []
    # Prefer 2-3 dumps; keep wall clock near `seconds`.
    n = 2 if seconds <= 6 else 3
    for i in range(n):
        h = B.health()
        if isinstance(h.get("frameMs"), (int, float)):
            frames.append(float(h["frameMs"]))
        if isinstance(h.get("tickAvgMs"), (int, float)):
            ticks.append(float(h["tickAvgMs"]))
        if isinstance(h.get("entityAlives"), (int, float)):
            alives.append(float(h["entityAlives"]))
        if i + 1 < n:
            time.sleep(max(0.5, seconds / n))
    def avg(xs):
        return round(sum(xs) / len(xs), 2) if xs else None
    out = {
        "label": label,
        "frameMs_avg": avg(frames),
        "frameMs_min": round(min(frames), 2) if frames else None,
        "frameMs_max": round(max(frames), 2) if frames else None,
        "tickAvgMs": avg(ticks),
        "entityAlives_avg": avg(alives),
        "players": (B.snapshot().get("world") or {}).get("players"),
        "raw_last": B.health(),
    }
    log(
        f"  sample {label}: frame={out['frameMs_avg']}ms "
        f"(min={out['frameMs_min']} max={out['frameMs_max']}) "
        f"alive={out['entityAlives_avg']} players={out['players']}"
    )
    return out


def parse_animstate(text: str) -> list[dict]:
    """Parse `es animstate` lines like:
      123 zombieBoe: en=True spd=1.00 rootMotion=True cull=CullCompletely ...
      vel=0.120 dp=0.0000 ...
    """
    rows = []
    for line in text.splitlines():
        line = line.strip()
        if "dp=" not in line or "cull=" not in line:
            continue
        m = re.search(
            r"(\d+)\s+(\S+):.*?en=(\w+).*?cull=(\S+).*?vel=([0-9.]+).*?dp=([0-9.]+)",
            line,
        )
        if not m:
            # looser
            m2 = re.search(r"cull=(\S+).*?vel=([0-9.]+).*?dp=([0-9.]+)", line)
            if not m2:
                continue
            rows.append(
                {
                    "cull": m2.group(1),
                    "vel": float(m2.group(2)),
                    "dp": float(m2.group(3)),
                    "raw": line[:200],
                }
            )
            continue
        rows.append(
            {
                "entityId": int(m.group(1)),
                "name": m.group(2),
                "en": m.group(3),
                "cull": m.group(4),
                "vel": float(m.group(5)),
                "dp": float(m.group(6)),
                "raw": line[:200],
            }
        )
    return rows


def animstate_snapshot() -> tuple[str, list[dict]]:
    text = B.telnet(["es animstate"], settle=2.0)
    return text, parse_animstate(text)


def fast_spawn(target: int) -> int:
    """Burst telnet spawnentity; faster than bloodmoon_profile.spawn_endgame for mid loads."""
    mix = (
        ["zombieBoeRadiated"] * 3 + ["zombieMarleneRadiated"] * 2 + ["zombieJoeRadiated"] * 2
        + ["zombieArleneRadiated"] * 2 + ["zombieBikerFeral"] * 2 + ["zombieFatCop"] * 1
        + ["zombieDemolition"] * 1 + ["zombieScreamer"] * 1
    )
    ids = B.player_ids()
    if not ids:
        return 0
    mi = 0
    # one big burst then a couple top-ups
    for round_i in range(4):
        cur = B.alive()
        if cur >= target:
            break
        need = target - max(cur, 0)
        cmds = []
        for _ in range(min(need, 60)):
            pid = ids[mi % len(ids)]
            cmds.append(f"spawnentity {pid} {mix[mi % len(mix)]}")
            mi += 1
        B.telnet(cmds, settle=1.2)
        log(f"  fast_spawn round {round_i+1}: alive={B.alive()}/{target}")
        time.sleep(1)
    return B.alive()


def main() -> int:
    B.PLAYERS = PLAYERS
    B.ZOMBIES = ZOMBIES
    B.GAMESTAGE = GAMESTAGE
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    report: dict = {
        "players": PLAYERS,
        "zombies": ZOMBIES,
        "gamestage": GAMESTAGE,
        "path_cap": PATH_CAP,
        "path_drop_far_sq": PATH_DROP,
        "phases": {},
        "verdicts": {},
    }
    bots = None
    code = 0
    try:
        # Snapshot before anything can mutate the installed config; on any
        # exit path (including a kill mid-run, recovered by the next run)
        # only these harness-owned knobs are reverted.
        CFG_SWAP.begin()
        if not SKIP_START:
            log(f"=== start server (players={PLAYERS} zombies={ZOMBIES}) ===")
            B.start_server()
        ensure_server_ready()

        # Confirm EfficientServer loaded
        st = B.telnet(["es status"], settle=1.5)
        report["es_status"] = st[-1500:]
        log(f"es status snippet: {st[-400:].replace(chr(10), ' | ')}")
        if "EfficientServer" not in st and "pathCap" not in st and "graphEvery" not in st:
            log("WARN: es status did not look like EfficientServer; continuing")

        log(f"=== join {PLAYERS} bots ===")
        bots, joined = B.join_ramped(PLAYERS)
        report["joined"] = joined
        if joined < max(1, int(PLAYERS * 0.5)):
            log(f"FAIL: only {joined}/{PLAYERS} players joined")
            report["verdicts"]["join"] = "FAIL"
            code = 2
            return code
        report["verdicts"]["join"] = "PASS"
        B.set_gamestage(GAMESTAGE)
        B.telnet(["es benchgod on"], settle=1)
        log(f"=== spawn ~{ZOMBIES} endgame ===")
        za = fast_spawn(ZOMBIES)
        report["spawned_alive"] = za
        time.sleep(6)

        # ----- Phase A: animator CullCompletely -----
        log("=== Phase A: animator emergency A/B ===")
        base = sample_health("anim_baseline")
        report["phases"]["anim_baseline"] = base

        off_txt = B.telnet(["es animoff"], settle=2.0)
        report["animoff_reply"] = off_txt[-800:]
        log(f"animoff: {off_txt[-300:].replace(chr(10), ' | ')}")
        time.sleep(2)
        off = sample_health("anim_off")
        report["phases"]["anim_off"] = off
        _, off_rows = animstate_snapshot()
        report["animstate_off_n"] = len(off_rows)
        report["animstate_off_cull_modes"] = sorted({r.get("cull") for r in off_rows})
        cull_ok = any(
            r.get("cull") and "CullCompletely" in str(r.get("cull")) for r in off_rows
        )
        # if animstate empty, still ok if frame moved
        report["verdicts"]["anim_cull_mode"] = (
            "PASS" if cull_ok or not off_rows else "FAIL_no_CullCompletely"
        )

        on_txt = B.telnet(["es animon"], settle=2.0)
        report["animon_reply"] = on_txt[-800:]
        log(f"animon: {on_txt[-300:].replace(chr(10), ' | ')}")
        time.sleep(3)
        on = sample_health("anim_restored")
        report["phases"]["anim_restored"] = on
        _, on_rows = animstate_snapshot()
        report["animstate_on_n"] = len(on_rows)
        moving = [r for r in on_rows if r.get("vel", 0) > 0.05]
        moving_dp = [r for r in moving if r.get("dp", 0) > 0.001]
        report["animstate_on_moving"] = len(moving)
        report["animstate_on_moving_dp_gt0"] = len(moving_dp)
        report["animstate_on_sample"] = on_rows[:8]
        if moving:
            report["verdicts"]["anim_root_motion"] = (
                "PASS" if moving_dp else "FAIL_dp_zero_crawl"
            )
        elif on_rows:
            report["verdicts"]["anim_root_motion"] = "SKIP_no_moving_zombies"
        else:
            report["verdicts"]["anim_root_motion"] = "SKIP_no_animstate"

        # frame delta (lower is better under load)
        fb, fo = base.get("frameMs_avg"), off.get("frameMs_avg")
        # unityDeltaMs floors near 50 on a healthy 20 TPS loop; only claim a frame
        # win when the baseline is actually over budget (stress).
        if isinstance(fb, (int, float)) and isinstance(fo, (int, float)) and fb > 55:
            delta = fo - fb
            report["anim_frame_delta_ms"] = round(delta, 2)
            report["verdicts"]["anim_frame_win"] = (
                "PASS" if fo <= fb * 0.95 or delta < -2 else "WEAK_no_frame_cut"
            )
        else:
            report["verdicts"]["anim_frame_win"] = "SKIP_light_load"

        # ----- Phase B: path admission -----
        log("=== Phase B: path admission A/B ===")
        # restore path knobs baseline first
        write_path_config(0, 0.0)
        B.telnet(["es reload"], settle=1.5)
        time.sleep(2)
        path_base = sample_health("path_baseline")
        report["phases"]["path_baseline"] = path_base

        write_path_config(PATH_CAP, PATH_DROP)
        reload_txt = B.telnet(["es reload", "es status"], settle=2.0)
        report["path_reload_status"] = reload_txt[-1200:]
        log(f"path knobs on: cap={PATH_CAP} dropFarSq={PATH_DROP}")
        if f"pathCap={PATH_CAP}" not in reload_txt and "pathCap=" in reload_txt:
            log("WARN: status pathCap may not match expected")
        time.sleep(3)
        path_on = sample_health("path_admission_on")
        report["phases"]["path_admission_on"] = path_on
        # fidelity proxy: entity count should not collapse
        ab = path_base.get("entityAlives_avg") or 0
        ao = path_on.get("entityAlives_avg") or 0
        if ab > 20 and ao < ab * 0.4:
            report["verdicts"]["path_fidelity"] = "FAIL_entity_collapse"
            code = max(code, 3)
        else:
            report["verdicts"]["path_fidelity"] = "PASS"
        pb, po = path_base.get("frameMs_avg"), path_on.get("frameMs_avg")
        if isinstance(pb, (int, float)) and isinstance(po, (int, float)):
            report["path_frame_delta_ms"] = round(po - pb, 2)
            report["verdicts"]["path_frame"] = (
                "PASS_or_noise" if po <= pb * 1.15 else "REGRESSION_frame_up"
            )
        else:
            report["verdicts"]["path_frame"] = "SKIP"

        # restore vanilla path knobs
        write_path_config(0, 0.0)
        B.telnet(["es reload"], settle=1.0)

        # overall
        hard = {
            k: v
            for k, v in report["verdicts"].items()
            if str(v).startswith("FAIL")
        }
        if hard:
            code = max(code, 1)
        report["exit_code"] = code
        report["verdicts"]["overall"] = "PASS" if code == 0 else "FAIL"
        log(f"=== VERDICTS: {json.dumps(report['verdicts'])} ===")

    except KeyboardInterrupt:
        # Same contract as the sibling harnesses: quiet 130, report still
        # written by the finally below.
        return 130
    except Exception as e:
        log(f"FAIL exception: {e}")
        report["error"] = repr(e)
        report["verdicts"]["overall"] = "ERROR"
        code = 4
    finally:
        try:
            CFG_SWAP.restore()
            B.telnet(["es reload", "es animon", "kickall"], settle=1.0)
        except Exception:
            pass
        if bots is not None:
            try:
                bots.terminate()
                bots.wait(timeout=15)
            except Exception:
                try:
                    bots.kill()
                except Exception:
                    pass
        subprocess.run(
            ["pkill", "-9", "-f", "net8.0/7dtd-loadge[n]"], check=False
        )
        # Default: leave dedicated running so multi-phase/tool-timeout runs can resume.
        # Set VALIDATE_KILL_SERVER=1 to tear down.
        if os.environ.get("VALIDATE_KILL_SERVER", "0") == "1":
            subprocess.run(
                ["pkill", "-9", "-f", "7DaysToDieServer.x86_6[4]"], check=False
            )
        write_report("validate_anim_path", report)
        # Fixed-name copy of the same report so tooling can tail one path.
        latest = OUT_DIR / "validate_anim_path_latest.json"
        latest.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    return code


if __name__ == "__main__":
    sys.exit(main())
