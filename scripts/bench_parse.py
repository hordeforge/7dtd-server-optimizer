#!/usr/bin/env python3
"""Parsers for the bench text this repo treats as untrusted input, and their fuzz gate.

Two parsers live here, both fed by text this repo does not control:

  * the APM health line the ``7dtd-server-apm`` bridge writes into the Unity
    server log (``read_apm``), sampled by ``measure_es_onoff.py``;
  * the ``es animstate`` console dump (``parse_animstate``), sampled by
    ``validate_anim_path_admission.py``.

Both were written inside the live-server harnesses, where importing them pulls
``harness_common`` and with it the 7dtd-loadgen sibling tree, so no gate could
reach them: ``python3 scripts/measure_es_onoff.py`` on a checkout without that
sibling exits inside ``harness_common`` before any of this code runs. Untrusted
parsing with no reachable test seam is a critical gap, so the parsers move here,
sibling-free, and the harnesses import them. The import is one-way: nothing in
this module imports a harness, so ``--selftest`` runs anywhere.

The fuzz contract is the property a caller depends on, not a crash count: a
line this module cannot understand must leave the caller with its previous good
value, never a raised exception and never a partially filled row. The A/B
verdicts are read straight off these values, so a parser that invents a number
fabricates an ON_faster / ON_slower call; a parser that raises kills the run and
loses the comparison entirely.

Deterministic (fixed seeds), so a failure reproduces from the iteration number
alone under ``make check-scripts`` with no fuzzing engine installed.
"""

from __future__ import annotations

import random
import re
import sys
import tempfile
import time
from collections.abc import Callable
from pathlib import Path
from typing import TypedDict

from cli_common import run_cli
from selftest_support import Checks

NAME = "scripts/bench_parse.py"
USAGE = """\
usage: scripts/bench_parse.py [-h | --help | --selftest]

Parsers for the [7dtd-server-apm] health line and the `es animstate` console
dump, plus the fuzz gate that drives them from hostile input. This module is
never an entry point of its own use; the live harnesses import the parsers.
"""

# The bridge prints cumulative averages rounded to two decimals
# (ToString("F2")), so every parsed value carries up to this much rounding
# error. windowed() divides the weighted-sum delta by the WINDOW update count
# while that error scales with TOTAL updates since boot, so the reconstruction
# bound grows with server uptime: sub-0.1 ms on a fresh boot, but an attached
# hours-old server (SKIP_SERVER_START=1) can push it well past the +/-0.5 ms
# verdict band - enough to fabricate an ON_faster/ON_slower call from print
# rounding alone. Verdicts must treat anything under the summed bounds as noise.
HALF_QUANTUM_MS = 0.005

# Bounded numeric fields, so a matching line always converts. A bare [0-9.]+
# accepts "." and "1.2.3", which float() then rejects, and an unbounded digit
# run accepts a 10_000-digit count, which int() rejects on Python 3.11+ (the
# int/str conversion limit). Both raise out of a parser that runs once per poll
# second on a file nobody in this repo wrote, so the bound is in the PATTERN:
# a line outside these widths is not understood, and not-understood is the
# documented fail-soft answer. Real values are small: a count fits in int64 and
# an average in F2 milliseconds.
# ASCII digits, spelled [0-9] rather than \d, which in Python matches every
# Unicode decimal digit: int() and float() both convert a run of Arabic-Indic
# or fullwidth digits, so a line carrying them read as real counters and then as
# a real measurement. The bridge writes ASCII, so such a line is unreadable, and
# unreadable is the answer the parser owes here.
_COUNTER_DIGITS = 18
_MS_INTEGER_DIGITS = 12
_MS_FRACTION_DIGITS = 6
_INT_FIELD = rf"[0-9]{{1,{_COUNTER_DIGITS}}}"
_MS_FIELD = rf"[0-9]{{1,{_MS_INTEGER_DIGITS}}}(?:\.[0-9]{{1,{_MS_FRACTION_DIGITS}}})?"

APM_LINE_RE = re.compile(
    rf"APM updates=({_INT_FIELD}) gmUpdateAvg=({_MS_FIELD})ms tickAvg=({_MS_FIELD})ms "
    rf"spikes=({_INT_FIELD})"
)

# A matching health line is well under 200 bytes. A partial line this long can
# never complete into one, so the carry buffer is capped at it: without the cap
# a log whose next newline is megabytes away (a half-written flush, a binary
# blob echoed into the log) parks every appended byte in memory for the rest of
# the run.
MAX_APM_LINE_BYTES = 4096

# One poll reads one poll-interval of appends, a few kB. The cap bounds the
# read on a log that grew by hundreds of MB while the harness was suspended
# (a paused VM, a stopped server): the remainder is picked up on the next call,
# so nothing is skipped, and no single call has to hold the whole gap. The
# offset advances by what was actually read, never by the file size.
MAX_APM_READ_BYTES = 1 << 20

# An `es animstate` row is one entity: id, name, animator flags and the three
# magnitudes, under 400 bytes. A line past this is not a row (it is a
# wrap-failed dump or a log echo), and scanning it is quadratic: both patterns
# retry from every start offset.
MAX_ANIM_LINE_CHARS = 4096


class ApmCounters(TypedDict):
    """Counters parsed out of one [7dtd-server-apm] health line (cumulative
    since server boot; see read_apm)."""

    updates: int
    gmUpdateAvg: float
    tickAvg: float
    spikes: int


# Incremental tail state per log path: which file was last read (device,
# inode and a sample of its head), the byte offset into it, the undecoded
# partial-line carry, and the newest matching APM health line seen so far. The
# Unity log is append-only and grows to hundreds of MB under blood-moon loads;
# rescanning the whole file every poll second would churn page cache and
# inject disk I/O noise into the exact frame-time numbers this harness
# measures.
class ApmTailState(TypedDict):
    dev: int
    ino: int
    head: bytes
    off: int
    tail: bytes
    last: ApmCounters | None


# Bytes of the file's head held to recognize a replacement that kept the size.
# A rotation to a log of exactly the length the offset had reached is otherwise
# invisible: the size test finds nothing new to read, so the caller gets the
# previous value (or None) for a file it has not read a single byte of.
_HEAD_SAMPLE_BYTES = 64


_APM_TAIL: dict[Path, ApmTailState] = {}

# Log paths whose stat() has already failed, so the warning is emitted once per
# path instead of once per poll second.
_APM_STAT_WARNED: set[Path] = set()


def _head_sample(logf: Path, size: int) -> bytes:
    """The first bytes of the log, for the replacement check (empty if it has none)."""
    if size == 0:
        return b""
    with logf.open("rb") as handle:
        return handle.read(min(size, _HEAD_SAMPLE_BYTES))


def _new_state() -> ApmTailState:
    return {
        "dev": 0,
        "ino": 0,
        "head": b"",
        "off": 0,
        "tail": b"",
        "last": None,
    }


def _counters(match: re.Match[str]) -> ApmCounters:
    """The four fields of a matched APM line, as counters.

    The pattern bounds every field, so these four conversions cannot fail; the
    conversions stay unguarded on purpose, because a ValueError here is a bug in
    APM_LINE_RE and the fuzz target is where it should surface.
    """
    return {
        "updates": int(match.group(1)),
        "gmUpdateAvg": float(match.group(2)),
        "tickAvg": float(match.group(3)),
        "spikes": int(match.group(4)),
    }


def reset_apm_tail(logf: Path | None = None) -> None:
    """Forget the incremental read state, for one path or all of them.

    Between runs the same path names a different log, so the carried offset and
    the last counters read belong to a file that is gone.
    """
    if logf is None:
        _APM_TAIL.clear()
        _APM_STAT_WARNED.clear()
        return
    _APM_TAIL.pop(logf, None)
    _APM_STAT_WARNED.discard(logf)


def apm_tail_bytes(logf: Path) -> int:
    """Bytes parked in the partial-line carry for `logf` (0 when unknown)."""
    st = _APM_TAIL.get(logf)
    return len(st["tail"]) if st else 0


def read_apm(logf: Path, warn: Callable[[str], None] | None = None) -> ApmCounters | None:
    """Parse the most recent matching [7dtd-server-apm] health line from the server log.

    Reads only bytes appended since the previous call (the log is append-only;
    state resets if the file was replaced, or shrank), so per-second polling
    costs one small read instead of a full-file rescan. Returns cumulative
    counters (updates, gmUpdateAvg, tickAvg, spikes); None before the first
    matching line ever.

    Fail-soft in both directions: bytes that are not a matching line leave the
    previous value in place (a line this parser cannot read is not evidence the
    server went quiet), and a line that matches is never applied in part.

    `warn` receives the vanished-log notice, once per path: the harness passes
    its own logger so the line keeps its UTC stamp in the run transcript. The
    notice is a courtesy for the reader, not part of the contract, so a caller
    with nowhere to put it can pass nothing.
    """
    st = _APM_TAIL.get(logf)
    try:
        stat = logf.stat()
    except OSError as e:
        # The log is gone (server stopped, or rotated to another path). Saying so
        # matters: the caller reads a None return as "no new data", so a
        # vanished log must not look like a quiet server, and returning the last
        # counters read silently would attribute them to a file that no longer
        # exists. windowed() sees updates stop growing and drops the window, so
        # the phase is recorded as absent rather than scored. Once per path, or
        # a stopped server prints it every poll second for the rest of the run.
        if warn is not None and logf not in _APM_STAT_WARNED:
            _APM_STAT_WARNED.add(logf)
            warn(f"  APM log stat failed ({e}); no further samples from {logf}")
        return st["last"] if st else None
    size = stat.st_size
    # A different file behind the same path, or a shorter one: the offset, the
    # carry and the remembered value all belong to bytes that are no longer
    # there, so start over. The head sample catches the case the size test
    # cannot see, a replacement that happens to be exactly as long as the
    # offset had reached.
    replaced = st is not None and (
        st["dev"] != stat.st_dev or st["ino"] != stat.st_ino or size < st["off"]
    )
    if st is None or replaced:
        st = _new_state()
        _APM_TAIL[logf] = st
    elif not st["head"] and size:
        st["head"] = _head_sample(logf, size)
    elif st["head"] and _head_sample(logf, size) != st["head"]:
        st = _new_state()
        _APM_TAIL[logf] = st
    st["dev"] = stat.st_dev
    st["ino"] = stat.st_ino
    if size > st["off"]:
        with logf.open("rb") as f:
            f.seek(st["off"])
            chunk = f.read(min(size - st["off"], MAX_APM_READ_BYTES))
        st["off"] += len(chunk)
        data = st["tail"] + chunk
        # Decode only complete lines; keep the unterminated tail as raw bytes
        # so a read boundary cannot split a line or a multibyte character.
        nl = data.rfind(b"\n")
        if nl >= 0:
            text = data[:nl].decode("utf-8", errors="replace")
            tail = data[nl + 1 :]
            st["tail"] = tail[-MAX_APM_LINE_BYTES:]
            for line in text.splitlines():
                if "[7dtd-server-apm]" not in line:
                    continue
                match = APM_LINE_RE.search(line)
                if match:
                    st["last"] = _counters(match)
        else:
            # No newline in this append: park the merged buffer (old carry
            # plus new bytes) back as the tail, or the offset above would
            # skip these bytes forever and truncate the line once it does
            # complete in a later append. Capped, because a line this long
            # cannot complete into a match.
            st["tail"] = data[-MAX_APM_LINE_BYTES:]
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


_ANIM_ROW_RE = re.compile(
    rf"([0-9]{{1,{_COUNTER_DIGITS}}})\s+(\S+):.*?en=(\w+).*?cull=(\S+)"
    rf".*?vel=({_MS_FIELD}).*?dp=({_MS_FIELD})"
)
_ANIM_LOOSE_RE = re.compile(rf"cull=(\S+).*?vel=({_MS_FIELD}).*?dp=({_MS_FIELD})")
_RAW_FIELD_CHARS = 200


def parse_animstate(text: str) -> list[dict[str, object]]:
    """Parse `es animstate` lines like:
      123 zombieBoe: en=True spd=1.00 rootMotion=True cull=CullCompletely ...
      vel=0.120 dp=0.0000 ...

    One line is one row, complete or absent: a line whose magnitudes do not
    parse is skipped whole rather than reported with a half-filled row, because
    the caller (a dp=0 crawl verdict) reads dp from every row it gets.
    """
    rows: list[dict[str, object]] = []
    for line in text.splitlines():
        line = line.strip()
        if len(line) > MAX_ANIM_LINE_CHARS:
            continue
        if "dp=" not in line or "cull=" not in line:
            continue
        match = _ANIM_ROW_RE.search(line)
        if match:
            rows.append(
                {
                    "entityId": int(match.group(1)),
                    "name": match.group(2),
                    "en": match.group(3),
                    "cull": match.group(4),
                    "vel": float(match.group(5)),
                    "dp": float(match.group(6)),
                    "raw": line[:_RAW_FIELD_CHARS],
                }
            )
            continue
        # looser: a row whose id or name the dump did not print, but whose
        # cull/vel/dp triple is intact.
        loose = _ANIM_LOOSE_RE.search(line)
        if loose:
            rows.append(
                {
                    "cull": loose.group(1),
                    "vel": float(loose.group(2)),
                    "dp": float(loose.group(3)),
                    "raw": line[:_RAW_FIELD_CHARS],
                }
            )
    return rows


#
# Fuzz gate
#

_APM_ITERATIONS = 2000
_ANIM_ITERATIONS = 2000
# Per-round ceiling, not a total-run budget. A round is a handful of appends
# plus a parse: microseconds on any host. The number a fixed-iteration fuzz can
# actually detect is a round that stopped returning, and 5s is far above any
# healthy round while a wedged one never clears it. A total ceiling measured the
# machine instead of the parser: 4000 rounds took 49.3s on a loaded Linux host,
# so a 30s budget failed a healthy run there and would have passed the same
# change on a faster box.
_FUZZ_MAX_ROUND_S = 5.0

# Non-digits and shapes a number never takes, spliced into a real health line.
# "." and "1.2.3" are the pair the old [0-9.]+ accepted and float() then
# rejected; the digit runs are counts int() refuses past the Python 3.11
# conversion limit; the Arabic-Indic digits are the shape str.isdigit() calls a
# number and int() CONVERTS (it has always accepted them), so only the pattern
# can keep them out. Every one of them is a line the parser must not
# understand, and none of them may raise.
_HOSTILE_NUMBERS = (
    ".",
    "..",
    "1.2.3",
    "1e999999",
    "1e-999999",
    "-1",
    "-0",
    "0x10",
    "",
    " 1",
    "1 ",
    "١٢٣",
    "1" * 5000,
    "9" * 5000,
    "1." * 900,
)
# Byte runs a log file meets in the wild: a mid-file BOM, a bad UTF-8
# continuation, a CESU-8 encoded surrogate, NUL and control bytes, and the two
# line-break spellings.
_HOSTILE_BYTES = (
    b"\x00",
    b"\x1f\x7f",
    b"\xc3\x28",
    b"\xed\xa0\x80",
    b"\xef\xbb\xbf",
    b"\xff\xfe",
    b"\r\n",
    b"\n\n",
    b"\xef\xbb\xbf[7dtd-server-apm] ",
)
# Token shapes that must not read as a magnitude or as a field value.
_HOSTILE_ANIM = (
    "dp=",
    "cull=",
    "vel=",
    "spd=NaN",
    "en=True",
    "zombieBoe:",
)


def _apm_line(updates: int, gm_ms: float, tick_ms: float, spikes: int) -> str:
    """One health line in the shape the bridge writes it (F2 milliseconds).

    The positive control the rounds below parse against, and the substrate the
    hostile mutations start from: without a known-good line in the corpus, a
    parser that matched nothing would pass every case.
    """
    return (
        f"[7dtd-server-apm] APM updates={updates} "
        f"gmUpdateAvg={gm_ms:.2f}ms tickAvg={tick_ms:.2f}ms spikes={spikes}"
    )


def _apm_expected(updates: int, gm_ms: float, tick_ms: float, spikes: int) -> ApmCounters:
    return {
        "updates": updates,
        "gmUpdateAvg": gm_ms,
        "tickAvg": tick_ms,
        "spikes": spikes,
    }


def _break_line(rng: random.Random, line: str) -> bytes:
    """A health line broken at the token layer or the byte layer."""
    data = line.encode("utf-8")
    for _ in range(rng.randint(1, 3)):
        edit = rng.randint(0, 6)
        if edit == 0:
            at = rng.randint(0, len(data))
            data = data[:at] + rng.choice(_HOSTILE_BYTES) + data[at:]
        elif edit == 1:
            data = data[: rng.randint(0, len(data))]
        elif edit == 2:
            data = data + rng.choice(_HOSTILE_BYTES) * rng.randint(1, 64)
        elif edit == 3 and data:
            # A single flipped byte. Skipped on an empty buffer: an earlier
            # edit in this round can have truncated the line to nothing, and
            # there is no byte left to flip.
            broken = bytearray(data)
            broken[rng.randrange(len(data))] = rng.randrange(0x100)
            data = bytes(broken)
        elif edit == 4:
            data = _splice_number(rng, data, b"gmUpdateAvg=", b"ms")
        elif edit == 5:
            data = _splice_number(rng, data, b"updates=", b" ")
        else:
            # Duplicate a field, so the pattern's left-to-right reading and a
            # human reading of the line disagree about which value is meant.
            cut = data.find(b"tickAvg=")
            if cut > 0:
                data = data[:cut] + data[cut : cut + 40] + data[cut:]
    return data


def _splice_number(rng: random.Random, data: bytes, label: bytes, stop: bytes) -> bytes:
    """Overwrite the value after `label` (up to `stop`) with hostile text."""
    at = data.find(label)
    if at < 0:
        return data
    start = at + len(label)
    end = data.find(stop, start)
    if end <= start:
        return data
    return data[:start] + rng.choice(_HOSTILE_NUMBERS).encode("utf-8") + data[end:]


def _break_row(rng: random.Random, row: str) -> str:
    """A console row broken at the token layer."""
    out = row
    for _ in range(rng.randint(1, 3)):
        edit = rng.randint(0, 5)
        if edit == 0:
            at = rng.randint(0, len(out))
            out = out[:at] + rng.choice(_HOSTILE_NUMBERS) + out[at:]
        elif edit == 1:
            at = rng.randint(0, len(out))
            out = out[:at] + rng.choice(_HOSTILE_ANIM) + out[at:]
        elif edit == 2:
            out = out[: rng.randint(0, len(out))]
        elif edit == 3:
            out = out + " " * rng.randint(0, 400) + "\n" + "dp=" + "9" * rng.randint(1, 300)
        elif edit == 4:
            out = _splice_text_number(rng, out, "dp=")
        else:
            out = out + "\n" + row
    return out


def _splice_text_number(rng: random.Random, text: str, label: str) -> str:
    at = text.find(label)
    if at < 0:
        return text
    start = at + len(label)
    end = text.find(" ", start)
    if end <= start:
        end = len(text)
    return text[:start] + rng.choice(_HOSTILE_NUMBERS) + text[end:]


def _note(failures: list[str], cond: bool, what: str) -> None:
    """Record a fuzz-invariant violation; the rounds print nothing per case."""
    if not cond:
        failures.append(what)


def _id_in_range(row: dict[str, object]) -> bool:
    """Whether a row carries no entity id, or one inside the field bounds."""
    value = row.get("entityId")
    if value is None:
        return True
    return isinstance(value, int) and 0 <= value < 10**_COUNTER_DIGITS


def _fuzz_apm(failures: list[str], root: Path, rng: random.Random, iteration: int) -> None:
    """One randomized append sequence against read_apm.

    Every write is an append, because that is the only transition the log
    really makes: rotation or truncation arrives as a SHRINK, which the parser
    has to notice, and a poll that lands mid-line is the case the carry buffer
    exists for. Each write is read back, so the state left by one hostile
    append is the state the next one is judged against.
    """
    logf = root / f"apm-{iteration}.txt"
    _write(logf, b"")
    reset_apm_tail(logf)
    for _ in range(rng.randint(1, 4)):
        roll = rng.randint(0, 9)
        if roll < 3:
            # A well-formed line: the parser must take it, and take it whole.
            _append(
                logf,
                (
                    _apm_line(
                        rng.randint(1, 5000),
                        round(rng.uniform(0.0, 40.0), 2),
                        round(rng.uniform(0.0, 60.0), 2),
                        rng.randint(0, 99),
                    )
                    + "\n"
                ).encode("utf-8"),
            )
        elif roll < 7:
            # A hostile line. Not necessarily unreadable: most byte mutations
            # leave a parseable line behind, and the parser is free to take it.
            # What it may never do is take it in part, or take a line the file
            # does not hold, which is what the rescan oracle below judges.
            _append(logf, _break_line(rng, _apm_line(rng.randint(1, 5000), 12.5, 30.25, 3)) + b"\n")
        elif roll < 8:
            # A newline-free append: carried, never decoded.
            _append(logf, b"x" * rng.randint(1, 200))
        elif roll < 9:
            # A boundary mid-line: half a health line, then the rest of it.
            whole = _apm_line(rng.randint(1, 5000), round(rng.uniform(0.0, 40.0), 2), 22.0, 4)
            half = len(whole) // 2
            _append(logf, whole[:half].encode("utf-8"))
            _check_apm(failures, read_apm(logf), logf, iteration)
            _append(logf, (whole[half:] + "\n").encode("utf-8"))
        else:
            # Shrink: the log was replaced, so the carried offset is void.
            _write(logf, (_apm_line(1, 5.0, 6.0, 0) + "\n").encode("utf-8"))
        got = read_apm(logf)
        _check_apm(failures, got, logf, iteration)
        # Idempotent: a second read with nothing appended returns the same
        # value, so per-second polling cannot drift the sample it reports.
        _note(
            failures,
            read_apm(logf) == got,
            f"iter {iteration}: read is not idempotent ({got} then {read_apm(logf)})",
        )
    _note(
        failures,
        apm_tail_bytes(logf) <= MAX_APM_LINE_BYTES,
        f"iter {iteration}: carry buffer grew to {apm_tail_bytes(logf)} bytes",
    )


def _append(logf: Path, data: bytes) -> None:
    with logf.open("ab") as handle:
        handle.write(data)


def _write(logf: Path, data: bytes) -> None:
    with logf.open("wb") as handle:
        handle.write(data)


def _rescan(logf: Path) -> ApmCounters | None:
    """The whole-file answer: the last complete matching line in the log.

    The oracle for the incremental read, and the reason the fuzz is not
    self-fulfilling: the incremental path keeps an offset, a carry buffer and a
    remembered value across calls, and this walks the file from byte zero with
    none of that state. The two must agree after every append, whatever the
    appends were. An unterminated final line is ignored on both sides, which is
    what the parser's carry buffer is for.
    """
    data = logf.read_bytes()
    cut = data.rfind(b"\n")
    if cut < 0:
        return None
    last: ApmCounters | None = None
    for line in data[: cut + 1].decode("utf-8", errors="replace").splitlines():
        match = APM_LINE_RE.search(line) if "[7dtd-server-apm]" in line else None
        if match:
            last = _counters(match)
    return last


def _check_apm(failures: list[str], got: ApmCounters | None, logf: Path, iteration: int) -> None:
    """The three properties an APM read has to hold after a hostile append.

    1. It agrees with a full rescan of the file (see _rescan), so the
       incremental state can neither lose a line the log holds nor invent one.
    2. Whatever comes back is complete and inside the field bounds: a counter
       the caller puts into a weighted sum must not be a fragment, and the
       A/B verdict is computed from it.
    3. Reading again with nothing appended returns the same value, so polling
       cannot drift the sample it reports.
    """
    expected = _rescan(logf)
    _note(
        failures,
        got == expected,
        f"iter {iteration}: {logf.name} read {got}, a rescan of the file gives {expected}",
    )
    if got is None:
        return
    # The TypedDict declares these as int/float, which is a promise the
    # regex groups have to keep, not something the runtime enforces, so the
    # range is what the fuzz pins: a field wide enough to hold anything else
    # would put a wrong number into the weighted sum behind the verdict.
    in_range = (
        0 <= got["updates"] < 10**_COUNTER_DIGITS
        and 0 <= got["spikes"] < 10**_COUNTER_DIGITS
        and 0.0 <= got["gmUpdateAvg"] < 10**_MS_INTEGER_DIGITS
        and 0.0 <= got["tickAvg"] < 10**_MS_INTEGER_DIGITS
    )
    _note(failures, in_range, f"iter {iteration}: {logf.name} read counters out of range: {got}")


def _fuzz_anim(failures: list[str], rng: random.Random, iteration: int) -> None:
    """Randomized `es animstate` dumps through parse_animstate.

    The property is per row: whatever comes back is whole (every field a
    verdict reads, from one line), finite, and inside the field bounds. A row
    assembled from half of one line and half of another is the failure this
    hunts, and a magnitude invented out of a malformed field is a fabricated
    dp reading, so the bounds are asserted per row, not per dump.
    """
    lines = [
        f"{rng.randint(0, 9999)} zombie{rng.randint(0, 9)}: en=True spd=1.00 "
        f"rootMotion=True cull=CullUpdates move=0 alive=True walk=1 "
        f"vel={rng.uniform(0, 2):.3f} dp={rng.uniform(0, 0.01):.4f} rmFwd=none"
        for _ in range(rng.randint(1, 3))
    ]
    lines += [
        _break_row(
            rng,
            f"{rng.randint(0, 9999)} zombieBoe: en=True spd=1.00 cull=AlwaysAnimate "
            f"vel={rng.uniform(0, 2):.3f} dp={rng.uniform(0, 0.01):.4f}",
        )
        for _ in range(rng.randint(1, 3))
    ]
    rows = parse_animstate("\n".join(lines))
    shapes = (
        {"entityId", "name", "en", "cull", "vel", "dp", "raw"},
        {"cull", "vel", "dp", "raw"},
    )
    _note(
        failures,
        all(set(row) in shapes for row in rows),
        f"iter {iteration}: a half-filled row came back: {rows}",
    )
    _note(
        failures,
        all(
            isinstance(row["vel"], float)
            and isinstance(row["dp"], float)
            and 0.0 <= float(row["vel"]) < 10**_MS_INTEGER_DIGITS
            and 0.0 <= float(row["dp"]) < 10**_MS_INTEGER_DIGITS
            for row in rows
        ),
        f"iter {iteration}: a magnitude outside the field bounds: {rows}",
    )
    _note(
        failures,
        all(_id_in_range(row) for row in rows),
        f"iter {iteration}: an entity id outside the field bounds: {rows}",
    )
    _note(
        failures,
        all(len(str(row["raw"])) <= _RAW_FIELD_CHARS for row in rows),
        f"iter {iteration}: a row carried more than the raw cap: {rows}",
    )


def _selftest() -> int:
    t = Checks("bench_parse")

    # Positive controls first: a parser that matched nothing would pass every
    # random round below, so the known-good shapes are pinned on their own.
    control = APM_LINE_RE.search(_apm_line(123, 12.34, 56.78, 9))
    t.check("apm: the control health line matches", control is not None)
    if control is not None:
        t.check(
            "apm: a bridge health line parses to its counters",
            _counters(control)
            == {"updates": 123, "gmUpdateAvg": 12.34, "tickAvg": 56.78, "spikes": 9},
        )
    t.check(
        "animstate: a console row parses to its fields",
        parse_animstate(
            "  412 zombieBoe: en=True spd=1.00 rootMotion=True cull=CullCompletely "
            "move=0 alive=True walk=1 vel=0.120 dp=0.0004 rmFwd=none"
        )
        == [
            {
                "entityId": 412,
                "name": "zombieBoe",
                "en": "True",
                "cull": "CullCompletely",
                "vel": 0.12,
                "dp": 0.0004,
                "raw": "412 zombieBoe: en=True spd=1.00 rootMotion=True "
                "cull=CullCompletely move=0 alive=True walk=1 vel=0.120 "
                "dp=0.0004 rmFwd=none",
            }
        ],
    )
    t.check(
        "animstate: a console line without cull/dp is not a row",
        parse_animstate("INFO: no world") == [],
    )
    # The shapes the unbounded patterns used to accept and the conversions
    # then rejected: unreadable is the answer, a raised ValueError is not.
    t.check(
        "apm: a malformed magnitude is not understood",
        APM_LINE_RE.search(_apm_line(1, 0.0, 0.0, 0).replace("0.00ms tickAvg", ".ms tickAvg"))
        is None,
    )
    t.check(
        "apm: an over-long count is not understood",
        APM_LINE_RE.search(_apm_line(1, 1.0, 1.0, 1).replace("updates=1", "updates=" + "9" * 5000))
        is None,
    )
    # int() and float() both convert a run of Arabic-Indic digits, so a line
    # carrying them read as a real counter under \d and windowed() then turned
    # it into a real measurement.
    arabic = "".join(chr(0x0660 + d) for d in (1, 2, 3))
    t.check(
        "apm: a count in non-ASCII digits is not understood",
        APM_LINE_RE.search(_apm_line(1, 1.0, 1.0, 1).replace("updates=1", "updates=" + arabic))
        is None,
    )
    t.check(
        "apm: a magnitude in non-ASCII digits is not understood",
        APM_LINE_RE.search(
            _apm_line(1, 1.0, 1.0, 1).replace("gmUpdateAvg=1.00", "gmUpdateAvg=" + arabic)
        )
        is None,
    )
    t.check(
        "animstate: an id in non-ASCII digits is not a row id",
        [
            r.get("entityId")
            for r in parse_animstate(arabic + " z: cull=CullCompletely vel=0.5 dp=0.25")
        ]
        == [None],
    )
    t.check(
        "animstate: a malformed magnitude is skipped whole",
        parse_animstate("1 z: cull=CullCompletely vel=. dp=.") == [],
    )
    t.check(
        "animstate: an over-long line is skipped, not scanned",
        parse_animstate("cull=X vel=1.0 dp=1.0 " + "a" * (MAX_ANIM_LINE_CHARS + 100)) == [],
    )
    t.check(
        "animstate: a row without an id still reports cull/vel/dp",
        parse_animstate("cull=CullCompletely vel=0.5 dp=0.25")
        == [
            {
                "cull": "CullCompletely",
                "vel": 0.5,
                "dp": 0.25,
                "raw": "cull=CullCompletely vel=0.5 dp=0.25",
            }
        ],
    )
    t.check(
        "windowed: a window over no new updates is no window",
        windowed(_apm_expected(10, 5.0, 6.0, 0), _apm_expected(10, 5.0, 6.0, 1)) is None,
    )
    flat = windowed(_apm_expected(10, 5.0, 6.0, 0), _apm_expected(20, 5.0, 6.0, 0))
    t.check(
        "windowed: an unchanged average reconstructs to itself, not noise",
        flat is not None
        and flat["gmUpdateAvg"] == 5.0
        and flat["gmUpdateAvg_err_ms"] == 0.015
        and flat["window_updates"] == 10,
    )
    t.check(
        "apm: a missing log reads as no data, and leaves no state",
        read_apm(Path(tempfile.gettempdir()) / "bench_parse-absent.log") is None,
    )

    # Random rounds over both parsers. Fixed seeds: a failure has to replay
    # from its iteration number alone, with no fuzzing engine installed.
    failures: list[str] = []
    slowest = 0.0
    fuzz_started = time.monotonic()
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        for iteration in range(_APM_ITERATIONS):
            round_started = time.monotonic()
            _fuzz_apm(failures, root, random.Random(0xA9_1CE + iteration), iteration)  # noqa: S311
            slowest = max(slowest, time.monotonic() - round_started)
        for iteration in range(_ANIM_ITERATIONS):
            round_started = time.monotonic()
            _fuzz_anim(failures, random.Random(0xA1_45 + iteration), iteration)  # noqa: S311
            slowest = max(slowest, time.monotonic() - round_started)
    for detail in failures[:10]:
        print("FAIL: fuzz: " + detail, file=sys.stderr)
    t.check(
        f"fuzz: {_APM_ITERATIONS} log and {_ANIM_ITERATIONS} console cases on hostile "
        f"input ({len(failures)} invariant violation(s))",
        not failures,
    )

    elapsed = time.monotonic() - fuzz_started
    t.check(
        f"fuzz: {_APM_ITERATIONS + _ANIM_ITERATIONS} rounds finished in {elapsed:.1f}s, "
        f"slowest round {slowest:.3f}s (ceiling {_FUZZ_MAX_ROUND_S:.0f}s)",
        slowest < _FUZZ_MAX_ROUND_S,
    )
    reset_apm_tail()
    return t.finish()


if __name__ == "__main__":
    run_cli(NAME, USAGE, _selftest, _selftest)
