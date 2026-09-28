#!/usr/bin/env python3
"""Incremental tail of the [7dtd-server-apm] health lines in a server log.

The Unity server log is append-only and grows to hundreds of MB under
blood-moon loads, so the harness reads only the bytes appended since the last
poll instead of rescanning the file. That makes the per-path tail state a
CACHE, and it has to be invalidated on more than "the file got smaller": the
path a run tails is fixed for the whole run (latest_server_log picks it once),
so a log recreated at that path, or one rolled over onto a fresh inode, hands
every later poll bytes from a DIFFERENT file at a byte offset that belongs to
the old one. The parsed counters then come from the middle of a foreign file
and the A/B verdict is computed from them. Identity (device + inode) is part
of the key, so a replaced file resets the state the same way a truncated one
does.

Split out of measure_es_onoff.py, which cannot be imported or executed without
a live dedicated server and the 7dtd-loadgen sibling, so the cache and its
invalidation had no test at all. Stdlib only: `make check-scripts` runs the
selftest here.
"""

from __future__ import annotations

import re
from collections.abc import Callable
from pathlib import Path
from typing import TypedDict

from cli_common import run_cli
from selftest_support import Checks

__all__ = [
    "HALF_QUANTUM_MS",
    "ApmCounters",
    "ApmSample",
    "ApmWindow",
    "read_apm",
    "windowed",
]

NAME = "scripts/apm_tail.py"
USAGE = """\
usage: scripts/apm_tail.py [-h | --help]
usage: scripts/apm_tail.py --selftest

Incremental APM-line tail of a server log (imported by
scripts/measure_es_onoff.py; run via that script).
  --selftest  drive the tail cache and the window math on synthetic logs\
"""

APM_LINE_RE = re.compile(
    r"APM updates=(\d+) gmUpdateAvg=([0-9.]+)ms tickAvg=([0-9.]+)ms spikes=(\d+)"
)

# The bridge prints cumulative averages rounded to two decimals
# (ToString("F2")), so every parsed value carries up to this much rounding
# error. windowed() divides the weighted-sum delta by the WINDOW update count
# while that error scales with TOTAL updates since boot, so the reconstruction
# bound grows with server uptime: sub-0.1 ms on a fresh boot, but an attached
# hours-old server (SKIP_SERVER_START=1) can push it well past the +/-0.5 ms
# verdict band - enough to fabricate an ON_faster/ON_slower call from print
# rounding alone. Verdicts must treat anything under the summed bounds as noise.
HALF_QUANTUM_MS = 0.005


# Counters parsed out of one [7dtd-server-apm] health line (cumulative since
# server boot; see read_apm).
class ApmCounters(TypedDict):
    updates: int
    gmUpdateAvg: float
    tickAvg: float
    spikes: int


# Incremental tail state per log path: the identity of the file those bytes
# came from, the byte offset read to, the undecoded partial-line carry, and the
# newest matching APM health line seen so far. `head` is the first bytes of the
# file this offset was measured against, re-checked on every poll.
class _ApmTailState(TypedDict):
    dev: int
    ino: int
    off: int
    head: bytes
    tail: bytes
    last: ApmCounters | None


# How much of the head of the log is kept to detect a rewrite in place. Unity
# writes a multi-line version/platform banner before any APM line, so a rotated
# or rewritten file is not the old bytes from offset 0 and 256 catches it
# while staying far below the per-poll read this cache exists to avoid.
HEAD_BYTES = 256

_APM_TAIL: dict[Path, _ApmTailState] = {}

# Log paths whose stat() has already failed, so the warning is emitted once per
# path instead of once per poll second.
_APM_STAT_WARNED: set[Path] = set()


def _head_bytes(logf: Path) -> bytes:
    with logf.open("rb") as f:
        return f.read(HEAD_BYTES)


def read_apm(logf: Path, log: Callable[..., None] = print) -> ApmCounters | None:
    """Parse the most recent matching [7dtd-server-apm] health line from the server log.

    Reads only bytes appended since the previous call (the log is append-only;
    the state resets if the file shrank or stopped being the file it was), so
    per-second polling costs one small read instead of a full-file rescan.
    Returns cumulative counters (updates, gmUpdateAvg, tickAvg, spikes); None
    before the first matching line ever.
    """
    st = _APM_TAIL.get(logf)
    try:
        info = logf.stat()
    except OSError as e:
        # The log is gone (server stopped, or rotated to another path). Say so
        # once per path: the caller reads this return value as "no new data", so
        # a vanished log must not look like a quiet server, and returning the
        # last counters read silently would attribute them to a file that no
        # longer exists. windowed() sees updates stop growing and drops the
        # window, so the phase is recorded as absent rather than scored.
        if logf not in _APM_STAT_WARNED:
            _APM_STAT_WARNED.add(logf)
            log(f"  APM log stat failed ({e}); no further samples from {logf}")
        return st["last"] if st else None
    # Identity alone is not a sufficient staleness test, and the offset alone is
    # not one either. A log rolled over onto this path (Unity renames the old
    # file away and starts a new one under the same name) OR truncated and
    # rewritten IN PLACE can already be LARGER than the offset cached for the
    # bytes it replaced, so the tail read would splice the new file's bytes onto
    # the old one's offset and parse counters belonging to neither file. The
    # stat identity catches the first, the head prefix the second, and either
    # resets the state the way a shrinking file does.
    replaced = st is not None and info.st_size < st["off"]
    rotated = st is not None and (info.st_dev != st["dev"] or info.st_ino != st["ino"])
    rewritten = st is not None and not replaced and not rotated and _head_bytes(logf) != st["head"]
    if st is None or replaced or rotated or rewritten:
        st = {
            "dev": info.st_dev,
            "ino": info.st_ino,
            "off": 0,
            "head": _head_bytes(logf),
            "tail": b"",
            "last": None,
        }
        _APM_TAIL[logf] = st
    if info.st_size > st["off"]:
        with logf.open("rb") as f:
            f.seek(st["off"])
            chunk = f.read(info.st_size - st["off"])
        st["off"] = info.st_size
        data = st["tail"] + chunk
        # Decode only complete lines; keep the unterminated tail as raw bytes
        # so a read boundary cannot split a line or a multibyte character.
        nl = data.rfind(b"\n")
        if nl >= 0:
            text = data[:nl].decode("utf-8", errors="replace")
            st["tail"] = data[nl + 1 :]
            for line in text.splitlines():
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
        else:
            # No newline in this append: park the merged buffer (old carry
            # plus new bytes) back as the tail, or the offset above would
            # skip these bytes forever and truncate the line once it does
            # complete in a later append.
            st["tail"] = data
    return st["last"]


class ApmWindow(TypedDict):
    """Windowed rate derived from two cumulative ApmCounters reads."""

    gmUpdateAvg: float
    gmUpdateAvg_err_ms: float
    tickAvg: float
    tickAvg_err_ms: float
    spikes: int
    window_updates: int


class ApmSample(ApmWindow):
    """An ApmWindow stamped with the phase label it was sampled under."""

    label: str


def windowed(a: ApmCounters, b: ApmCounters) -> ApmWindow | None:
    """Windowed (instantaneous-ish) metrics from two cumulative APM reads.

    gmUpdateAvg / tickAvg are cumulative since boot; the per-window value is
    the delta of the weighted sums over the updates in between. Returns None
    when the window covers no new updates (e.g. a quiet server).

    *_err_ms is the worst-case reconstruction error from the bridge's
    two-decimal formatting: half a quantum on each cumulative average,
    amplified by (total updates / window updates). Any verdict must compare
    deltas against the SUMMED error bounds of both phases, not raw digits.
    """
    du = b["updates"] - a["updates"]
    if du <= 0:
        return None

    def recon(avg_a: float, avg_b: float) -> tuple[float, float]:
        sa = a["updates"] * avg_a
        sb = b["updates"] * avg_b
        err = HALF_QUANTUM_MS * (a["updates"] + b["updates"]) / du
        return round((sb - sa) / du, 3), err

    gm, gm_err = recon(a["gmUpdateAvg"], b["gmUpdateAvg"])
    tick, tick_err = recon(a["tickAvg"], b["tickAvg"])
    return {
        "gmUpdateAvg": gm,
        "gmUpdateAvg_err_ms": round(gm_err, 3),
        "tickAvg": tick,
        "tickAvg_err_ms": round(tick_err, 3),
        "spikes": b["spikes"],
        "window_updates": du,
    }


def _line(updates: int, gm: float, tick: float, spikes: int = 0) -> str:
    return (
        f"2026.09.28 12:00:00 -> [7dtd-server-apm] "
        f"APM updates={updates} gmUpdateAvg={gm}ms tickAvg={tick}ms spikes={spikes}\n"
    )


def _selftest() -> int:
    import tempfile

    t = Checks("apm_tail")
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        logf = root / "server_prefab_world__20260928.txt"

        # 1. No matching line yet: None, and the state records the file identity.
        logf.write_text("2026.09.28 12:00:00 -> plain server line\n", encoding="utf-8")
        t.check("no APM line yet reads as None", read_apm(logf) is None)

        # 2. The first matching line is parsed.
        with logf.open("a", encoding="utf-8") as f:
            f.write(_line(10, 50.0, 50.0))
        first = read_apm(logf)
        t.check("first APM line parses", first is not None and first["updates"] == 10)

        # 3. A partial line split across two reads is carried, not lost: the
        # offset advances, so without the carry the completed line would be
        # skipped and the harness would report a silent bridge forever.
        with logf.open("a", encoding="utf-8") as f:
            f.write("[7dtd-server-apm] APM updates=20 gmUpda")
        t.check("a split line is not parsed early", read_apm(logf) == first)
        with logf.open("a", encoding="utf-8") as f:
            f.write("teAvg=60.00ms tickAvg=55.00ms spikes=1\n")
        second = read_apm(logf)
        t.check(
            "the completed line parses across the read boundary",
            second is not None and second["updates"] == 20 and second["spikes"] == 1,
        )

        # 4. A later non-APM append leaves the last counters alone.
        with logf.open("a", encoding="utf-8") as f:
            f.write("2026.09.28 12:00:05 -> noise\n")
        t.check("a non-APM append keeps the last counters", read_apm(logf) == second)

        # 5. A REPLACEMENT at the same path, larger than the offset cached for
        # the file it replaced. The offset test alone passes this case, so the
        # tail would be read from a byte position belonging to the old file and
        # the parsed counters would belong to neither. The new file's own first
        # line must win.
        replaced = _line(3, 11.0, 9.0) + "pad" * 4096
        logf.write_text(replaced, encoding="utf-8")
        third = read_apm(logf)
        t.check(
            "a log replaced at the same path is re-read from its start",
            third is not None and third["updates"] == 3 and third["gmUpdateAvg"] == 11.0,
        )

        # 5b. A ROLLOVER: the old file renamed away and a new one created under
        # the same name, which is how the Unity logger starts the next log. The
        # new file opens with a fresh inode, and here it is also bigger than
        # the cached offset, so both the size test and the head-prefix test are
        # passed by an identity-blind cache.
        logf.rename(root / "server_prefab_world__20260928.txt.1")
        logf.write_text(_line(5, 33.0, 31.0) + "x" * 8192, encoding="utf-8")
        after_rollover = read_apm(logf)
        t.check(
            "a log rolled over onto the same path is re-read from its start",
            after_rollover is not None
            and after_rollover["updates"] == 5
            and after_rollover["gmUpdateAvg"] == 33.0,
        )

        # 6. Truncation to smaller than the offset resets the same way.
        logf.write_text(_line(7, 21.0, 19.0), encoding="utf-8")
        after_shrink = read_apm(logf)
        t.check(
            "a truncated log is re-read from its start",
            after_shrink is not None and after_shrink["updates"] == 7,
        )

        # 7. A vanished log reports once per path and hands back no data, so a
        # caller cannot score a phase off a file that no longer exists. The
        # read happens on a one-second poll, so an un-announced miss would
        # repeat forever.
        def quiet(*_a: object, **_k: object) -> None:
            return None

        logf.unlink()
        gone = root / "server_prefab_world__20260929.txt"
        warned: list[str] = []
        t.check("a missing log reads as None", read_apm(gone, log=warned.append) is None)
        read_apm(gone, log=warned.append)
        t.check("a missing log is announced exactly once", len(warned) == 1)

    # 8. Window math: cumulative counters reconstruct the windowed rate, and a
    # window with no new updates is None rather than a zero-rate phase.
    a: ApmCounters = {
        "updates": 100,
        "gmUpdateAvg": 50.0,
        "tickAvg": 45.0,
        "spikes": 2,
    }
    b: ApmCounters = {
        "updates": 200,
        "gmUpdateAvg": 60.0,
        "tickAvg": 55.0,
        "spikes": 3,
    }
    w = windowed(a, b)
    t.check(
        "window reconstructs the rate between two cumulative reads",
        w is not None and w["gmUpdateAvg"] == 70.0 and w["tickAvg"] == 65.0,
    )
    t.check("window reports the update span", w is not None and w["window_updates"] == 100)
    t.check(
        "window carries the reconstruction error bound",
        w is not None and w["gmUpdateAvg_err_ms"] == round(HALF_QUANTUM_MS * 300 / 100, 3),
    )
    t.check("a window with no new updates is None", windowed(a, a) is None)
    t.check("a window against an earlier read is None", windowed(b, a) is None)

    return t.finish()


if __name__ == "__main__":
    run_cli(NAME, USAGE, _selftest, _selftest)
