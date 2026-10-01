#!/usr/bin/env python3
"""Rerun-safe backup/restore cycle for the installed EfficientServer config.

The bench harnesses rewrite a few keys in the LIVE installed config
(``Mods/EfficientServer/Config/efficientserver.json``) and must put them back
even when a run is killed mid-experiment; tool timeouts and SIGKILL are routine
for these long server-backed runs. A plain copy-backup has a destructive
failure mode: a backup left behind by a killed run gets restored by a LATER,
unrelated run after the operator has already repaired or re-tuned the config,
silently reverting everything done in between.

Guard protocol (every step idempotent under repetition):

- ``recover()``: handle a backup left behind by an earlier killed run.
    * If the live file equals the backup except for this swap's managed keys,
      the divergence can only come from a run of this same harness that died
      before restoring: finish its interrupted restore (copy back, drop backup).
    * Otherwise the live file moved on since (operator edit, reinstall, valid
      JSON of some other shape): the backup is stale evidence. Rename it to
      ``<backup>.stale`` and touch nothing - never restore across unrelated
      edits.
- ``begin()``: ``recover()``, then snapshot the live file byte-exact.
  Safe to call again while already begun (no re-snapshot).
- ``restore()``: write ONLY the managed keys (absence included) from the
  snapshot back into the live file, then delete it. Other keys keep whatever
  is on disk now, so a restore cannot clobber newer operator tuning. A second
  call is a no-op. Exception: if the live file is missing, unreadable, or not
  a JSON object, the full snapshot is restored instead (a managed-keys-only
  rebuild would destroy or misplace every other operator setting).

The backup is named after the OWNING RUN's pid (``<config>.swap-bak<pid>``),
so two harnesses pointed at one install never share a snapshot and cannot
restore each other's. ``recover()`` resolves backups no live run owns: a dead
pid's, and any name without a parseable pid (a backup from before backups were
pid-scoped). A live pid's backup is left strictly alone, because that run is
mid-experiment and its snapshot is the only copy of the pre-run config.

All writes go through temp-file + rename so a kill mid-write cannot leave a
truncated JSON behind for the game's config reader or the next run.
"""

from __future__ import annotations

import contextlib
import copy
import json
import os
import random
import re
import stat
import sys
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import NoReturn

from cli_common import run_cli
from selftest_support import Checks

# The installed efficientserver.json is operator-editable and the game's own
# reader (Config.Load) decodes it as UTF-8 with a leading BOM tolerated, so a
# BOM is a legal config, not corruption. utf-8-sig is a strict superset of
# utf-8: it decodes BOM-less files identically and strips a BOM when present.
# Reading these configs as plain utf-8 made every reader here throw
# "Unexpected UTF-8 BOM", which recover() then treated as a damaged file and
# quarantined, and restore() treated as unreadable and overwrote wholesale.
CFG_ENCODING = "utf-8-sig"

STALE_SUFFIX = ".stale"
# Marker write_atomic puts between a file name and its writer's pid, so a
# temp stranded by a killed run is attributable to that run. The pid is
# followed by an attempt counter; _temp_owner parses both halves.
TEMP_INFIX = ".tmp"
# The backup marker, carrying the SAME owning pid the temp names do. One
# backup per RUN rather than one per install: a single fixed backup path is
# shared by every harness pointed at the install, so two concurrent runs
# snapshot into the same file and each restores the other's snapshot. See
# ConfigSwap for the interleaving; _backup_owner parses the pid.
BAK_INFIX = ".swap-bak"
# How many temp names one write may try before giving up. Each attempt is a
# name this call did not create yet, so exhausting them means something is
# squatting on every name (see _write_atomic), which is a fail-loud condition.
TEMP_ATTEMPTS = 8
# How many quarantined (`.stale`) backups of one config are kept beside it. The
# quarantine name is suffix-resolved so a repeat never destroys the previous
# run's evidence, which without a bound means the LIVE install directory grows
# one file per killed harness run forever (see ConfigSwap._prune_quarantines).
# A handful covers the burst a single bench session produces; the point is that
# the count is finite, not that it is large.
STALE_KEEP = 5


# A pid as it appears in a temp name: plain ASCII digits, and few enough of them
# that the value always fits the C int os.kill takes. str.isdigit() is not that
# test: it accepts superscript and non-ASCII digits, so "²" passes it and then
# raises ValueError in int(), and an unbounded digit run parses fine and then
# raises OverflowError in os.kill. The name being parsed is whatever sits in the
# LIVE install directory or the operator's backup destination, not a name this
# process minted, so the parse has to reject those instead of raising out of
# the sweep.
_OWNER_PID_RE = re.compile(r"\A[0-9]{1,9}\Z")
# The sweep only uses the value for a signal-0 liveness probe, so a pid the
# kernel could never have assigned is unparseable for the same reason a
# non-numeric one is. This is the Linux pid_max ceiling (2^22); it is a bound
# on the PARSE, not a policy about the host.
_MAX_OWNER_PID = 4_194_304


def owner_pid(text: str) -> int | None:
    """``text`` as a pid this host could have assigned, or None.

    The one pid parse behind every pid-in-a-filename sweep, shared with
    backup_config's staging sweep so both directories are read by the same
    rule. Non-ASCII digits and digit runs past the C int are rejected here
    rather than raising out of the caller's sweep.
    """
    if _OWNER_PID_RE.match(text) is None:
        return None
    pid = int(text)
    return pid if pid <= _MAX_OWNER_PID else None


def _temp_owner(name: str, base_name: str) -> int | None:
    """The pid that wrote temp ``name`` of ``base_name``, or None when unparseable."""
    if not name.startswith(base_name + TEMP_INFIX):
        return None
    tail = name[len(base_name) + len(TEMP_INFIX) :]
    return owner_pid(tail.split("_", 1)[0])


def _backup_owner(name: str, base_name: str) -> int | None:
    """The pid that owns backup ``name`` of ``base_name``, or None when unparseable.

    None alone cannot tell a name that is not this protocol's backup at all from
    a backup written by a build from before backups were pid-scoped, so the
    caller resolves that second case itself: :meth:`ConfigSwap._orphan_backups`
    treats any backup-shaped name with no parseable pid as an orphan to recover,
    which is what makes a run started by the older build still recoverable.
    """
    if not name.startswith(base_name + BAK_INFIX):
        return None
    return owner_pid(name[len(base_name) + len(BAK_INFIX) :])


def _pid_alive(pid: int) -> bool:
    """Whether ``pid`` is a live process this host could still signal.

    A signal-0 probe, the same liveness test the temp sweep uses, so the two
    sweeps cannot disagree about what "still running" means. ``OSError`` other
    than ProcessLookupError (EPERM on a process owned by another user) means
    the pid EXISTS but is not ours to signal: alive, and never ours to remove.
    """
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except OSError:
        return True
    return True


USAGE = """\
usage: scripts/es_cfg_guard.py [--selftest] [-h | --help]

Backup/restore guard library for the installed EfficientServer config
(imported by the bench harnesses), plus a self-test of that protocol.
  --selftest  run the protocol self-test (default with no arguments)
  -h, --help  show this help\
"""


def _read_doc(path: Path) -> dict[str, object]:
    # Boundary pin: json.loads is typed Any; the guard protocol only ever
    # feeds it the object-shaped efficientserver.json.
    doc: dict[str, object] = json.loads(path.read_text(encoding=CFG_ENCODING))
    return doc


def _canonical(node: object) -> str:
    """Order-independent text form of a document, for equality and failure lines.

    Takes `object` so the fuzz projection (which can yield a scalar or a list)
    and the dict-shaped protocol path share one spelling.
    """
    return json.dumps(node, sort_keys=True, separators=(",", ":"))


def _write_atomic(path: Path, data: bytes) -> None:
    # The temp name is fully predictable (base name, this process's pid, a
    # counter), and it lands in the LIVE install directory, so anything else on
    # the host can pre-plant that name before this call runs. O_CREAT|O_EXCL
    # fails on an existing entry instead of following it, so a pre-planted
    # symlink cannot redirect the write to a file this process can write but
    # does not own: each attempt takes the next counter, and exhausting them
    # raises rather than picking a name someone else owns.
    tmp: Path | None = None
    fd = -1
    for attempt in range(TEMP_ATTEMPTS):
        candidate = path.with_name(f"{path.name}{TEMP_INFIX}{os.getpid()}_{attempt}")
        try:
            fd = os.open(candidate, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        except FileExistsError:
            continue
        tmp = candidate
        break
    else:
        msg = f"every atomic-write temp name for {path} is taken; refusing to write"
        raise FileExistsError(msg)
    try:
        # fdopen takes ownership of the descriptor only once it RETURNS: the
        # FileIO object is what closes it, and it does not exist if the call
        # raises. Unwinding from there leaves the raw descriptor open for the
        # life of the process while the unlink below removes the name it was
        # written under, so the inode survives with nothing left pointing at
        # it. Close it here, then re-raise the original failure untouched.
        try:
            sink = os.fdopen(fd, "wb")
        except BaseException:
            with contextlib.suppress(OSError):
                os.close(fd)
            raise
        with sink:
            sink.write(data)
        # Carry the live file's own permissions over, so an operator who
        # tightened them (or a umask that made the original 0600) does not get
        # a silently widened file; a target that does not exist yet keeps the
        # owner-only mode the temp was created with.
        with contextlib.suppress(OSError):
            tmp.chmod(stat.S_IMODE(path.stat().st_mode))
        tmp.replace(path)
    except BaseException:
        # The temp file is this call's only copy of the data until the rename
        # lands, and a failed write (ENOSPC, EACCES on the target directory) or
        # a failed replace (cross-device, target is a directory, read-only fs)
        # leaves it stranded next to the live config for every later run to
        # trip over. Drop it, then let the original error propagate unchanged.
        with contextlib.suppress(OSError):
            tmp.unlink()
        raise


def unique_path(path: Path) -> Path:
    """Return ``path``, or the first free ``<stem>.<n><suffix>`` beside it.

    Every artifact this repo names after a wall-clock stamp (report JSON,
    quarantined backup) is stamped to the SECOND, and a run that is retried
    inside the same second - a tool-timeout kill followed by an immediate
    re-run, two legs of an A/B pair back to back - resolves to the same name.
    The second write then silently destroys the first one's output, which is
    the opposite of what a retry should do. This resolves the collision by
    suffixing, so a repeat ADDS an artifact instead of replacing one.

    The scan is read-then-write, not atomic: two harnesses racing the same
    free name can still both pick it, and the later rename wins. That race is
    between processes deliberately run side by side, and bounding it needs a
    lock whose failure modes are worse than the collision it prevents; the
    sequential-retry case, which is what actually happens, is covered.
    """
    if not path.exists():
        return path
    n = 1
    while True:
        candidate = path.with_name(f"{path.stem}.{n}{path.suffix}")
        if not candidate.exists():
            return candidate
        n += 1


# The collision counter unique_path puts in a suffixed name, as it appears
# after the base name: ".7" of "efficientserver.json.swap-bak.7.stale". ASCII
# digits only for the same reason as _OWNER_PID_RE: these names live in the
# operator's install directory, so the parse rejects what it cannot read
# rather than raising out of the sweep.
_UNIQUE_SUFFIX_RE = re.compile(r"\A\.(?P<n>[0-9]{1,9})\Z")


def _quarantine_key(bak_name: str, name: str) -> tuple[int, int, str]:
    """Age key for one quarantine file of the config whose backup is ``bak_name``.

    ``unique_path`` numbers a suffixed name, so the sequence is
    ``<bak>.stale``, ``<bak>.1.stale``, ... ``<bak>.10.stale``. Sorting those by
    NAME alone puts ``.10`` before ``.2`` and ``.1`` before ``.10``, so
    ConfigSwap._prune_quarantines kept the OLDEST evidence and destroyed the
    newest whenever the mtimes it compares could not separate the files: on a
    coarse-mtime filesystem, a burst of quarantines inside one second all
    timestamp identically, and the twelve-quarantine burst that reproduces it
    retained copies 0, 3, 4, 5 and 11 and dropped 6 through 10. The counter is
    a number, so it is compared as one; the name is the last tie-break, so two
    files the counter cannot order still have a deterministic one.

    A file whose name does not decompose into this backup's name plus
    STALE_SUFFIX sorts after every real quarantine, and is never selected as
    one of the newest to keep.
    """
    stem = name.removesuffix(STALE_SUFFIX)
    if not stem.startswith(bak_name):
        return (1, 0, name)
    tail = stem[len(bak_name) :]
    m = _UNIQUE_SUFFIX_RE.fullmatch(tail)
    if m is not None:
        return (0, int(m.group("n")), name)
    return (0, 0, name) if not tail else (1, 0, name)


def _quarantined_backup_name(name: str) -> str:
    """The backup name a quarantine file was renamed from.

    Strips STALE_SUFFIX and the optional ``.<n>`` collision counter
    unique_path adds, so ``cfg.swap-bak42.3.stale`` yields ``cfg.swap-bak42``.
    A pid follows BAK_INFIX with no dot, so the counter cannot be confused
    with it.
    """
    stem = name.removesuffix(STALE_SUFFIX)
    head, dot, tail = stem.rpartition(".")
    return head if dot and _UNIQUE_SUFFIX_RE.fullmatch(dot + tail) else stem


def write_atomic(path: Path, data: str) -> None:
    """Replace ``path`` with ``data`` atomically (temp file + rename), UTF-8.

    The crash-safe twin of ``Path.write_text``: a kill mid-write cannot leave a
    truncated file behind. Anything rewriting the LIVE installed config must go
    through this (the guard's own writes already do), or a tool-timeout SIGKILL
    reintroduces exactly the truncated-JSON state the backup/restore protocol
    exists to prevent - and the next run then quarantines the backup instead of
    being able to finish a restore.
    """
    _write_atomic(path, data.encode("utf-8"))


class ConfigSwap:
    """Backup-modify-restore for selected keys of one JSON config file.

    Each managed key is a path tuple (``("Pathfinding", "MaxPathEnqueuesPerTick")``),
    root-level keys included: ``("Enabled",)``. A bare string is not accepted, because
    the path walk would then address one character per step.
    """

    def __init__(
        self,
        cfg_path: Path,
        keys: Sequence[tuple[str, ...]],
        log: Callable[..., None] = print,
    ):
        self.cfg = cfg_path
        self.keys = keys
        self._log = log
        self._begun = False
        # This run's OWN backup, carrying its pid. Per-run, not per-install:
        # a single shared backup path let two concurrent runs snapshot into
        # the same file, so each one's restore replayed the OTHER's snapshot.
        # The interleaving, with A beginning first and B second:
        #
        #   A  begin()      -> bak = snapshot of the pre-A config
        #   A  rewrites the managed keys it is testing
        #   B  begin()      -> recover() finds A's bak, and since the live
        #                       file diverges from it on exactly the managed
        #                       keys, concludes A DIED mid-restore, replays
        #                       A's snapshot over the live config, and unlinks
        #                       the backup A still needs
        #   B  begin()      -> snapshots the now-A-restored config into the
        #                       same path
        #   A  restore()    -> restores B's snapshot, silently reverting the
        #                       operator's file to B's pre-run state
        #
        # and the worse ordering, where A's restore lands while B is mid-test:
        #
        #   A  restore()    -> writes A's snapshot back over B's in-flight
        #                       config, so the measured run is corrupted
        #
        # Neither interleaving needs a kill; both just need two harnesses on
        # one install, which the tool already documents as a real case (the
        # temp sweep below leaves a live pid's files alone for exactly that
        # reason). One backup per run closes both: A and B never name the
        # same file.
        self.bak = cfg_path.with_name(f"{cfg_path.name}{BAK_INFIX}{os.getpid()}")

    def _orphan_backups(self) -> list[Path]:
        """Backups no live run owns: a killed run's, or a pre-pid-scoping one.

        These are exactly what ``recover()`` exists to resolve. A backup whose
        pid is still running is EXCLUDED: that run is mid-experiment, its
        snapshot is the only copy of the pre-run config, and replaying or
        deleting it is the cross-run corruption the per-run name prevents. A
        name with no parseable pid is included rather than skipped: that is a
        backup written by the build from before backups carried their owner's
        pid, and it is just as abandoned.
        """
        orphans: list[Path] = []
        for candidate in self.cfg.parent.glob(f"{self.cfg.name}{BAK_INFIX}*"):
            if candidate == self.bak or candidate.name.endswith(STALE_SUFFIX):
                continue  # ours, or quarantined evidence a prior run set aside
            owner = _backup_owner(candidate.name, self.cfg.name)
            if owner is not None and _pid_alive(owner):
                continue  # a concurrent run's, not an orphan
            orphans.append(candidate)
        return orphans

    # -- key helpers -------------------------------------------------------

    def _get(self, doc: dict[str, object], kp: tuple[str, ...]) -> tuple[bool, object]:
        node: object = doc
        for k in kp[:-1]:
            if not isinstance(node, dict) or k not in node:
                return False, None
            node = node[k]
        if not isinstance(node, dict) or kp[-1] not in node:
            return False, None
        return True, node[kp[-1]]

    def _set(
        self,
        doc: dict[str, object],
        kp: tuple[str, ...],
        present: bool,
        value: object,
    ) -> None:
        node = doc
        for k in kp[:-1]:
            child = node.get(k)
            if not isinstance(child, dict):
                child = {}
                node[k] = child
            node = child
        if present:
            node[kp[-1]] = value
        else:
            node.pop(kp[-1], None)

    # -- protocol ----------------------------------------------------------

    def _sweep_abandoned_temps(self) -> None:
        """Delete the atomic-write temp files killed runs stranded beside the config.

        ``write_atomic`` names its temp ``<file>.tmp<pid>_<attempt>`` and renames
        it into place, so a run SIGKILLed (or power-cut) between the write and
        the rename leaves one file per killed run in the LIVE install directory,
        under a name nothing in the protocol ever matches again. These runs are
        routinely killed by tool timeouts, so a bench host accumulates them
        without bound. A temp whose owning pid is gone cannot be an in-flight
        write, so it is garbage; one whose pid is still running (a concurrent
        harness, or a recycled pid) is left alone. The pid is the whole
        attributable identity: only that decides liveness, never the attempt
        counter, and a name with no leading pid is not a temp of this protocol
        at all and is left untouched.

        Swept for EVERY backup base in the directory, not just this run's: a
        killed run strands a temp beside ITS backup, and that backup is named
        after its own pid, so a sweep scoped to the current run's name would
        leave one stranded temp per killed run forever.
        """
        bases = [self.cfg.name, self.bak.name]
        bases.extend(
            c.name
            for c in self.cfg.parent.glob(f"{self.cfg.name}{BAK_INFIX}*")
            if c.name not in bases and not c.name.endswith(STALE_SUFFIX)
        )
        for base_name in bases:
            for tmp in self.cfg.parent.glob(f"{base_name}{TEMP_INFIX}*"):
                owner = _temp_owner(tmp.name, base_name)
                if owner is None or _pid_alive(owner):
                    continue
                tmp.unlink(missing_ok=True)

    def _quarantine(self, why: str, bak: Path | None = None) -> None:
        # Suffix-resolved: two quarantines in the same run's lifetime (or two
        # runs of a bench loop that keeps hitting damaged state) must not have
        # the second rename destroy the first one's evidence.
        target = self.bak if bak is None else bak
        stale = unique_path(target.with_suffix(target.suffix + STALE_SUFFIX))
        target.replace(stale)
        self._log(
            f"config guard: leftover backup {target.name} is stale ({why}); "
            f"kept as evidence at {stale.name}, live file NOT touched"
        )
        self._prune_quarantines()

    def _prune_quarantines(self) -> None:
        """Keep only the newest STALE_KEEP quarantines of this config.

        The quarantine name is suffix-resolved, so a bench loop that keeps
        hitting damaged state writes one `.stale` per run and NOTHING ever
        removes them: they pile up in the LIVE install directory, which the
        game reads on every boot and install.sh preserves verbatim on every
        reinstall. The evidence a run needs is the most recent few (which
        config was live when the divergence appeared); older ones cannot
        describe a state any later run can still be compared against, because
        the config has moved on since.

        Bounded by count, not age: a burst of kills inside one bench session is
        exactly when the operator wants the history, and it is the NEXT session
        that has no use for it. Ordered by mtime, with the collision counter
        unique_path assigned as the tie-break (see _quarantine_key), so the
        newest rename wins a tie on a coarse-mtime filesystem.

        Counted across EVERY run's quarantines of this config, not just this
        run's: backups are named after their owning pid, so a bound scoped to
        this run's name would see only its own files and the directory would
        still grow by one per killed run.
        """
        parent = self.cfg.parent
        found: list[tuple[int, tuple[int, int, str], Path]] = []
        for path in parent.glob(f"{self.cfg.name}{BAK_INFIX}*{STALE_SUFFIX}"):
            if not path.is_file():
                continue
            try:
                # st_mtime_ns, not st_mtime: the float seconds lose the
                # sub-second ordering on a coarse-mtime filesystem, so a burst
                # of kills inside one second ties and falls to the key below.
                found.append(
                    (
                        path.stat().st_mtime_ns,
                        _quarantine_key(_quarantined_backup_name(path.name), path.name),
                        path,
                    )
                )
            except OSError:
                continue  # swept out from under this scan
        if len(found) <= STALE_KEEP:
            return
        found.sort(key=lambda row: (row[0], row[1]))
        doomed = found[: len(found) - STALE_KEEP]
        # One boundary around the whole drop, not one per file: a file already
        # gone is not a failure of the sweep, and naming every one of them would
        # bury the count that matters.
        try:
            for _mtime, _key, path in doomed:
                path.unlink(missing_ok=True)
        except OSError as ex:
            self._log(
                f"config guard: pruning old quarantined backups for {self.bak.name}"
                f" stopped early ({ex}); the rest are still on disk"
            )
            return
        self._log(
            f"config guard: pruned up to {len(doomed)} quarantined backup(s) beyond"
            f" the newest {STALE_KEEP} for {self.bak.name}"
        )

    def recover(self) -> None:
        """Resolve a backup left behind by an earlier killed run.

        Considers this run's own backup and every ORPHAN (a dead owner's, or
        one written before backups were pid-scoped), and never a backup a live
        pid owns: that run is mid-experiment, and replaying or deleting its
        snapshot is the cross-run corruption the per-run backup name exists to
        prevent. Several orphans resolve in name order so the outcome does not
        depend on directory iteration order.
        """
        self._sweep_abandoned_temps()
        pending = ([self.bak] if self.bak.is_file() else []) + sorted(
            self._orphan_backups(), key=lambda p: p.name
        )
        for bak in pending:
            self._recover_one(bak)

    def _recover_one(self, bak: Path) -> None:
        """Resolve one orphaned (or this run's) backup against the live config."""
        if not bak.is_file():
            return
        try:
            bak_doc = _read_doc(bak)
            bak_bytes = bak.read_bytes()
        except Exception as e:
            self._quarantine(f"unreadable: {e}", bak)
            return
        if not self.cfg.is_file():
            self._quarantine("live config missing", bak)
            return
        try:
            # Not _read_doc: the live document's SHAPE is unknown here (that is
            # what the check below decides), so parse untyped like the boundary
            # json.loads is.
            live: object = json.loads(self.cfg.read_text(encoding=CFG_ENCODING))
        except Exception as e:
            self._quarantine(f"live config unreadable: {e}", bak)
            return
        # A VALID-JSON but non-object live document (array, scalar, null) parses
        # yet has no keys to replay managed values into: the divergence rule
        # cannot apply, so quarantine like any other damaged state - restoring
        # across a shape an operator (or a corrupting writer) produced is never
        # this guard's call.
        if not isinstance(live, dict):
            self._quarantine(
                f"live config is valid JSON but not an object ({type(live).__name__})", bak
            )
            return
        # Replay the backup's managed-key values onto the live doc; if that
        # makes the documents identical, only this harness touched the file
        # since the snapshot -> finish the interrupted restore.
        replayed = copy.deepcopy(live)
        for kp in self.keys:
            present, value = self._get(bak_doc, kp)
            self._set(replayed, kp, present, value)
        if _canonical(replayed) == _canonical(bak_doc):
            _write_atomic(self.cfg, bak_bytes)
            bak.unlink()
            self._log(
                f"config guard: finished restore from backup {bak.name} "
                "left by a killed earlier run"
            )
        else:
            self._quarantine("live config changed beyond managed keys since snapshot", bak)

    def begin(self) -> None:
        """Snapshot the live file for later restore (idempotent once begun)."""
        self._sweep_abandoned_temps()
        if self._begun and self.bak.is_file():
            return
        # Resolve any backup a killed earlier run left before snapshotting.
        # A concurrent run's own backup is untouched (recover skips live pids),
        # so two runs on one install each keep the snapshot they need.
        self.recover()
        if not self.cfg.is_file():
            msg = f"missing {self.cfg}"
            raise FileNotFoundError(msg)
        _write_atomic(self.bak, self.cfg.read_bytes())
        self._begun = True
        self._log(f"config guard: snapshotted {self.cfg.name} -> {self.bak.name}")

    def restore(self) -> None:
        """Put the managed keys back to their snapshotted values (idempotent)."""
        if not self.bak.is_file():
            return
        try:
            bak_bytes = self.bak.read_bytes()
            # Not _read_doc, same reason as the live document below: the
            # snapshot's SHAPE is not an object either until it is checked.
            bak: object = json.loads(bak_bytes.decode(CFG_ENCODING))
        except Exception as e:
            self._log(f"config guard: RESTORE FAILED, backup kept ({e})")
            return
        if not isinstance(bak, dict):
            # A snapshot that is not a JSON object has no managed key to read
            # back: the key-scoped walk below would read every managed key as
            # absent and STRIP it from the live config, which is the opposite
            # of a restore. Same full-snapshot exit the damaged live cases take.
            _write_atomic(self.cfg, bak_bytes)
            self.bak.unlink()
            self._log(
                f"config guard: snapshot is valid JSON but not an object "
                f"({type(bak).__name__}); restored full backup"
            )
            return
        bak_doc = bak
        if not self.cfg.is_file():
            _write_atomic(self.cfg, bak_bytes)
            self.bak.unlink()
            self._log("config guard: live config was missing; restored full backup")
            return
        try:
            # Not _read_doc, same reason as recover(): the live shape is what
            # the check below decides.
            live: object = json.loads(self.cfg.read_text(encoding=CFG_ENCODING))
        except Exception as e:
            # Unreadable live JSON: rebuilding from {} would write back ONLY the
            # managed keys and destroy every other operator setting, so fall
            # back to the exact snapshot instead.
            _write_atomic(self.cfg, bak_bytes)
            self.bak.unlink()
            self._log(f"config guard: live config unreadable ({e}); restored full backup")
            return
        if not isinstance(live, dict):
            # Valid JSON of the wrong shape (array, scalar, null): a key-scoped
            # rebuild would likewise destroy or misplace every operator setting,
            # so take the same full-snapshot exit as the unreadable case.
            _write_atomic(self.cfg, bak_bytes)
            self.bak.unlink()
            self._log(
                f"config guard: live config is valid JSON but not an object "
                f"({type(live).__name__}); restored full backup"
            )
            return
        for kp in self.keys:
            present, value = self._get(bak_doc, kp)
            self._set(live, kp, present, value)
        _write_atomic(self.cfg, (json.dumps(live, indent=2) + "\n").encode("utf-8"))
        self.bak.unlink()
        self._begun = False
        self._log(f"config guard: restored managed keys from {self.bak.name}")


# Fuzz target for the same untrusted input the selftest drives one damaged
# state at a time: the installed efficientserver.json. It is operator-editable,
# ships inside a mod package, and both the game's Config.Load and this guard
# decode it on every run. The fixtures above pin one shape each; this drives
# random combinations of a hostile live document, a hostile leftover backup and
# a random protocol step order, on a fixed seed so a failure reproduces under
# `make test` with no fuzzing host.
#
# Invariants asserted per case, each of them something a hostile document must
# not be able to do to the LIVE install:
#   1. No protocol step raises, whatever the live and backup bytes are. The one
#      documented exception, begin() on a missing config, still raises.
#   2. recover() consumes the backup and leaves the live file either exactly as
#      it was or exactly as the snapshot was: a restore, never a third state.
#      This is the pair assertion across the persistence boundary.
#   3. restore() consumes the backup and, on the key-scoped path, changes
#      nothing outside the managed paths: every other key of the pre-restore
#      live document is still there with the same value. A rebuild from {} would
#      pass the corrupt-live fixture and still eat operator tuning on a merely
#      unusual live document.
#   4. begin() snapshots the live file byte-exact.
#   5. No step strands an atomic-write temp beside the config.
_FUZZ_ITERATIONS = 400
_FUZZ_KEYS: list[tuple[str, ...]] = [
    ("Pathfinding", "MaxPathEnqueuesPerTick"),
    ("Pathfinding", "DropPathWhenFarDistSq"),
    ("Enabled",),
]
_FUZZ_SECTIONS = ("Pathfinding", "Network", "Gc", "Governor")
# Key names a hand-edited file really carries: the real ones, a case-flipped
# twin, a dotted path spelled flat, an empty and a whitespace name, non-ASCII,
# and a leading space the C# reader would not see as the same knob.
_FUZZ_KEY_NAMES = (
    "Enabled",
    "enabled",
    "Enabled ",
    "Pathfinding.MaxPathEnqueuesPerTick",
    "",
    " ",
    "サーバ",
)
# Value shapes a lenient reader accepts where a number belongs, plus the
# structural swaps that turn a managed key into a node no key walk can enter.
_FUZZ_HOSTILE_VALUES: tuple[object, ...] = (
    None,
    True,
    [],
    [1, 2, 3],
    {},
    {"nested": {"deep": [1]}},
    "1e999999",
    "NaN",
    "",
    "サーバ café \U0001f600",
    -1,
    2**31 - 1,
    -(2**31),
)
# Byte-level damage applied to an otherwise well-formed document: a BOM, a
# truncated copy, a half-saved file, junk spliced mid-document, invalid UTF-8,
# and nesting past any sane reader depth.
_FUZZ_BYTE_RUNS = (
    b"",
    b"\xef\xbb\xbf",
    b"\xff\xfe\x80\x81",
    b"{\x00\x1f\x7f",
    b'"\\ud800"',
    b"1e999999",
    b"}]}}" * 4,
    b"[[" * 64 + b"]]" * 64,
)


def _fuzz_doc(rng: random.Random) -> dict[str, object]:
    """A well-formed config document with random operator values and knobs.

    Hostile shapes land on the managed paths and on near-miss key names, which
    is where the key walk has to tell a real key from its typo'd twin.
    """
    doc: dict[str, object] = {}
    for section in _FUZZ_SECTIONS:
        node: dict[str, object] = {
            "GraphUpdateEveryTicks": rng.randint(-(2**31), 2**31 - 1),
            "MaxPathEnqueuesPerTick": rng.randint(0, 2000),
            "Note": rng.choice(["", "サーバ café \U0001f600", "x" * rng.randint(0, 64)]),
        }
        if rng.random() < 0.2:
            # A managed key holding a value the key walk can still address, but
            # of a shape the game reader has to clamp.
            node["DropPathWhenFarDistSq"] = rng.choice(_FUZZ_HOSTILE_VALUES)
        doc[section] = node
    doc["Enabled"] = rng.choice([True, False])
    doc["Operator"] = {"Tier": rng.randint(0, 9), "Name": "ops"}
    if rng.random() < 0.3:
        doc[rng.choice(_FUZZ_KEY_NAMES)] = rng.choice(_FUZZ_HOSTILE_VALUES)
    if rng.random() < 0.1:
        # A section turned into something the key walk cannot descend into.
        doc[_FUZZ_SECTIONS[0]] = rng.choice(_FUZZ_HOSTILE_VALUES)
    return doc


def _fuzz_bytes(rng: random.Random, doc: dict[str, object]) -> bytes:
    """Serialize `doc`, then splice, cut and corrupt it at the byte layer."""
    data = (json.dumps(doc, indent=2) + "\n").encode("utf-8")
    for _ in range(rng.randint(0, 3)):
        edit = rng.randint(0, 4)
        if edit == 0:
            data = rng.choice(_FUZZ_BYTE_RUNS) + data
        elif edit == 1:
            data += rng.choice(_FUZZ_BYTE_RUNS)
        elif edit == 2:
            at = rng.randint(0, len(data))
            data = data[:at] + rng.choice(_FUZZ_BYTE_RUNS) + data[at:]
        elif edit == 3 and data:
            data = data[: rng.randint(0, len(data))]
        else:
            at = rng.randint(0, len(data))
            data = data[:at] + bytes(rng.randrange(0x80, 0x100) for _ in range(4)) + data[at:]
    return data


def _fuzz_backup_bytes(rng: random.Random) -> bytes | None:
    """Bytes for the leftover backup: absent, intact, or damaged every way."""
    choice = rng.randint(0, 6)
    if choice == 0:
        return None
    if choice == 1:
        return _fuzz_bytes(rng, _fuzz_doc(rng))
    if choice == 2:
        return b""
    if choice == 3:
        return rng.choice([b"[1, 2]", b"null", b'"text"', b"42", b"true"])
    if choice == 4:
        return b"{ truncated"
    if choice == 5:
        return b"\xef\xbb\xbf" + (json.dumps(_fuzz_doc(rng))).encode("utf-8")
    return _fuzz_bytes(rng, _fuzz_doc(rng))[: rng.randint(0, 40)]


def _fuzz_owned(path: tuple[str, ...], keys: Sequence[tuple[str, ...]]) -> bool:
    """True when `path` is a managed key, an ancestor of one, or inside one."""
    return any(path[: len(kp)] == kp or kp[: len(path)] == path for kp in keys)


def _fuzz_unmanaged(
    node: object,
    keys: Sequence[tuple[str, ...]],
    path: tuple[str, ...] = (),
) -> object:
    """`node` with every managed path and everything under one removed.

    The projection the harness owns; comparing it before and after a protocol
    step is what proves an operator key outside the managed scope survived.
    """
    if not isinstance(node, dict):
        return node
    return {
        k: _fuzz_unmanaged(v, keys, (*path, str(k)))
        for k, v in node.items()
        if not _fuzz_owned((*path, str(k)), keys)
    }


def _fuzz_json(data: bytes | None) -> tuple[bool, object]:
    """`(decoded, document)` for `data`; `(False, None)` when it will not parse.

    The flag is separate from the value because a JSON `null` document decodes
    to Python None, which is a real shape the protocol has to handle and not the
    same thing as bytes nobody can read.
    """
    if data is None:
        return False, None
    try:
        return True, json.loads(data.decode(CFG_ENCODING))
    except (UnicodeDecodeError, ValueError):
        return False, None


def _fuzz_temp_litter(root: Path) -> list[str]:
    return sorted(p.name for p in root.iterdir() if TEMP_INFIX in p.name)


def _fuzz_protocol(failures: list[str], iteration: int) -> None:
    """One randomized protocol case. Appends to `failures`; never raises."""
    import tempfile

    # Fixed per iteration, not cryptographic: reproducibility is the point, a
    # failing case has to replay from its iteration number alone.
    rng = random.Random(0xC0FFEE + iteration)  # noqa: S311
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        cfg = root / "efficientserver.json"
        swap = ConfigSwap(cfg, _FUZZ_KEYS, log=lambda _m: None)
        live_bytes = _fuzz_bytes(rng, _fuzz_doc(rng))
        cfg.write_bytes(live_bytes)
        backup_bytes = _fuzz_backup_bytes(rng)
        if backup_bytes is None:
            swap.bak.unlink(missing_ok=True)
        else:
            swap.bak.write_bytes(backup_bytes)

        steps = rng.choice(
            [
                ["recover"],
                ["restore"],
                ["begin", "restore"],
                ["begin", "begin", "restore"],
                ["recover", "begin", "restore"],
                ["restore", "restore"],
            ]
        )
        for step in steps:
            pre_live = cfg.read_bytes() if cfg.is_file() else None
            pre_backup = swap.bak.read_bytes() if swap.bak.is_file() else None
            try:
                if step == "begin":
                    # A missing live config is the one documented raise; it
                    # surfaces through the FileNotFoundError arm below.
                    swap.begin()
                    if pre_live is not None and (
                        not swap.bak.is_file() or swap.bak.read_bytes() != pre_live
                    ):
                        failures.append(f"iter {iteration}: begin did not snapshot the live file")
                elif step == "recover":
                    swap.recover()
                    if pre_backup is not None and swap.bak.is_file():
                        failures.append(f"iter {iteration}: recover left the backup behind")
                    if pre_backup is not None and pre_live is not None:
                        post = cfg.read_bytes() if cfg.is_file() else None
                        if post not in (pre_live, pre_backup):
                            failures.append(f"iter {iteration}: recover left a third live state")
                    elif pre_backup is not None and cfg.is_file():
                        failures.append(f"iter {iteration}: recover resurrected a missing config")
                else:
                    swap.restore()
                    if pre_backup is None:
                        if cfg.is_file() and cfg.read_bytes() != pre_live:
                            failures.append(f"iter {iteration}: restore wrote with no backup")
                        continue
                    # A snapshot that decodes to something other than a JSON
                    # object has no managed key to read back: the guard takes
                    # its full-snapshot exit, exactly as it does for a damaged
                    # live document. A snapshot that does not decode at all is
                    # kept untouched instead: it is the only copy of the
                    # pre-run state, so nothing may overwrite with it.
                    snapshot_decoded, snapshot = _fuzz_json(pre_backup)
                    if not snapshot_decoded:
                        if not swap.bak.is_file():
                            failures.append(
                                f"iter {iteration}: restore dropped an undecodable "
                                "snapshot instead of keeping it"
                            )
                        if cfg.is_file() and cfg.read_bytes() != pre_live:
                            failures.append(
                                f"iter {iteration}: restore wrote over the live "
                                "config with an undecodable snapshot"
                            )
                        continue
                    if not isinstance(snapshot, dict):
                        if swap.bak.is_file() or cfg.read_bytes() != pre_backup:
                            failures.append(
                                f"iter {iteration}: non-object snapshot not taken "
                                "as the full restore"
                            )
                        continue
                    if swap.bak.is_file():
                        failures.append(f"iter {iteration}: restore left the backup behind")
                    post = cfg.read_bytes() if cfg.is_file() else None
                    live_decoded, live_doc = _fuzz_json(pre_live)
                    if not live_decoded or not isinstance(live_doc, dict):
                        # A live document the key-scoped walk cannot address:
                        # the full snapshot must be written back verbatim.
                        if post != pre_backup:
                            failures.append(
                                f"iter {iteration}: unusable live config not "
                                "restored from the full snapshot"
                            )
                        continue
                    after_decoded, after_doc = _fuzz_json(post)
                    if not after_decoded or not isinstance(after_doc, dict):
                        failures.append(f"iter {iteration}: restore left an unreadable config")
                    elif _fuzz_unmanaged(after_doc, _FUZZ_KEYS) != _fuzz_unmanaged(
                        live_doc, _FUZZ_KEYS
                    ):
                        failures.append(
                            f"iter {iteration}: restore changed keys outside the "
                            "managed scope ("
                            f"{_canonical(_fuzz_unmanaged(after_doc, _FUZZ_KEYS))}"
                            f" vs {_canonical(_fuzz_unmanaged(live_doc, _FUZZ_KEYS))})"
                        )
            except FileNotFoundError:
                if step != "begin" or pre_live is not None:
                    failures.append(
                        f"iter {iteration}: {step} raised FileNotFoundError on a "
                        "present live config"
                    )
            except Exception as exc:  # a hostile file must fail soft, never raise
                failures.append(f"iter {iteration}: {step} raised {type(exc).__name__}: {exc}")
            litter = _fuzz_temp_litter(root)
            if litter:
                failures.append(f"iter {iteration}: {step} stranded temp files {litter}")


def _selftest() -> int:
    import tempfile

    t = Checks("es_cfg_guard")

    def section(doc: dict[str, object], key: str) -> dict[str, object]:
        """Narrow a top-level JSON object to one of its nested sections."""
        sub = doc[key]
        if not isinstance(sub, dict):
            msg = f"config section {key!r} is not a JSON object"
            raise TypeError(msg)
        return sub

    keys = [
        ("Pathfinding", "MaxPathEnqueuesPerTick"),
        ("Pathfinding", "DropPathWhenFarDistSq"),
        ("Enabled",),
    ]
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        original = {
            "Enabled": True,
            "Pathfinding": {"GraphUpdateEveryTicks": 4, "MaxPathEnqueuesPerTick": 0},
            "Network": {"EntityDistributionEveryTicks": 1},
        }
        cfg = root / "efficientserver.json"
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")

        logs: list[str] = []

        def mk() -> ConfigSwap:
            return ConfigSwap(cfg, keys, log=logs.append)

        # 1. begin/restore roundtrip restores exact bytes.
        s = mk()
        s.begin()
        doc = _read_doc(cfg)
        doc["Enabled"] = False
        section(doc, "Pathfinding")["MaxPathEnqueuesPerTick"] = 64
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        s.restore()
        expected = json.dumps(original, indent=2).encode("utf-8") + b"\n"
        t.check("roundtrip restores exact bytes", cfg.read_bytes() == expected)

        # 2. crash simulation: a NEW instance finishes the interrupted restore.
        s = mk()
        s.begin()
        doc = _read_doc(cfg)
        doc["Enabled"] = False
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        mk().recover()  # next run, before any mutation
        t.check("crashed run recovered", _canonical(_read_doc(cfg)) == _canonical(original))
        t.check("recovery consumed backup", not s.bak.exists())

        # 3. stale backup: live diverged beyond managed keys -> untouched.
        s.begin()
        doc = _read_doc(cfg)
        section(doc, "Network")["EntityDistributionEveryTicks"] = 3  # operator edit
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        mk().recover()
        stale = s.bak.with_suffix(s.bak.suffix + STALE_SUFFIX)
        t.check("stale quarantined", stale.is_file() and not s.bak.exists())
        t.check(
            "stale recover leaves live untouched",
            section(_read_doc(cfg), "Network")["EntityDistributionEveryTicks"] == 3,
        )

        # 4. restore is key-scoped and repeat-safe; absence is preserved too.
        s2 = mk()
        s2.begin()  # snapshots the operator-edited file
        stale.unlink()
        doc = _read_doc(cfg)
        doc["Enabled"] = False
        section(doc, "Pathfinding")["DropPathWhenFarDistSq"] = 2500  # key absent in snapshot
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        s2.restore()
        s2.restore()  # second call must be a no-op
        after = _read_doc(cfg)
        t.check(
            "key-scoped restore keeps other keys",
            section(after, "Network")["EntityDistributionEveryTicks"] == 3,
        )
        t.check("restore reverts managed key", after["Enabled"] is True)
        t.check(
            "restore removes key absent in snapshot",
            "DropPathWhenFarDistSq" not in section(after, "Pathfinding"),
        )
        t.check("repeat restore is a no-op", not s2.bak.exists())

        # 5. double begin does not re-snapshot over modified state.
        s3 = mk()
        s3.begin()
        doc = _read_doc(cfg)
        doc["Enabled"] = False
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        s3.begin()
        s3.restore()
        t.check("begin is sticky until restored", _read_doc(cfg)["Enabled"] is True)

        # 6. live file deleted between begin and restore -> full backup restore
        # (not just managed keys, which would produce a truncated config).
        s4 = mk()
        s4.begin()
        snapshot = _canonical(_read_doc(cfg))
        cfg.unlink()
        s4.restore()
        t.check(
            "missing live config restored from full backup",
            cfg.is_file() and _canonical(_read_doc(cfg)) == snapshot,
        )
        t.check("backup consumed after missing-live restore", not s4.bak.exists())

        # 7. live file UNREADABLE (corrupt JSON) between begin and restore ->
        # full backup restore too: key-scoped restore onto {} would silently
        # drop every non-managed operator key.
        s5 = mk()
        s5.begin()
        snapshot = _canonical(_read_doc(cfg))
        cfg.write_text("{ this is not json ][", encoding="utf-8")
        s5.restore()
        t.check(
            "corrupt live config restored from full backup",
            _canonical(_read_doc(cfg)) == snapshot,
        )
        t.check("backup consumed after corrupt-live restore", not s5.bak.exists())

        # 8. A UTF-8 BOM is a legal installed config: the game's Config.Load
        # decodes with Encoding.UTF8 and tolerates one, so every path here must
        # read it too. Reading it as plain utf-8 raised "Unexpected UTF-8
        # BOM", which quarantined a good backup and turned restore() into the
        # unreadable-live full-overwrite branch. Non-ASCII values must also
        # survive the backup/restore round trip byte for byte.
        bom_cfg = root / "bom.json"
        bom_doc = {"Enabled": True, "Note": "サーバ café \U0001f600"}
        bom_cfg.write_bytes(
            b"\xef\xbb\xbf" + (json.dumps(bom_doc, indent=2) + "\n").encode("utf-8")
        )
        mk_bom = ConfigSwap(bom_cfg, [("Enabled",)], log=logs.append)
        bom_bak = mk_bom.bak
        bom_bak.write_bytes(bom_cfg.read_bytes())
        # recover(): a BOM'd live file whose only divergence is a managed key
        # is an interrupted run, not stale evidence.
        bom_live = dict(bom_doc, Enabled=False)
        bom_cfg.write_bytes(
            b"\xef\xbb\xbf" + (json.dumps(bom_live, indent=2) + "\n").encode("utf-8")
        )
        mk_bom.recover()
        t.check(
            "BOM'd live config finishes an interrupted restore, not a quarantine",
            _canonical(_read_doc(bom_cfg)) == _canonical(bom_doc) and not bom_bak.exists(),
        )

        # restore(): key-scoped restore over a BOM'd live file. "Operator" is
        # present only in the live file, never in the backup, so it survives
        # ONLY on the key-scoped path. A BOM that fails to parse takes the
        # unreadable-live branch instead and silently drops it.
        bom_live2 = dict(bom_live, Operator="later edit")
        bom_cfg.write_bytes(
            b"\xef\xbb\xbf" + (json.dumps(bom_live2, indent=2) + "\n").encode("utf-8")
        )
        # recover() above consumed the backup. Re-snapshot the ORIGINAL document
        # (Enabled=true, no "Operator"): the live file now carries the harness's
        # managed-key edit plus an operator edit the backup never saw.
        bom_bak.write_bytes(
            b"\xef\xbb\xbf" + (json.dumps(bom_doc, indent=2) + "\n").encode("utf-8")
        )
        bom2 = ConfigSwap(bom_cfg, [("Enabled",)], log=logs.append)
        bom2.restore()
        after_bom = _read_doc(bom_cfg)
        t.check(
            "BOM'd live config restores key-scoped, keeping later operator edits",
            after_bom["Enabled"] is True and after_bom["Operator"] == "later edit",
        )
        t.check(
            "non-ASCII config value survives backup/restore",
            after_bom["Note"] == bom_doc["Note"],
        )

        # 9. public atomic write: create, overwrite, exact bytes, no temp litter.
        wa = root / "atomic.json"
        write_atomic(wa, '{"k": 1}\n')
        write_atomic(wa, '{"k": 2}\n')
        t.check(
            "atomic write creates and overwrites",
            wa.read_text(encoding="utf-8") == '{"k": 2}\n',
        )
        t.check(
            "atomic write leaves no temp files",
            [p.name for p in root.iterdir() if ".tmp" in p.name] == [],
        )

        # 8c. unique_path: a second write to the same stamped name must ADD a
        # file, never replace the first. A run is retried inside the same
        # second (tool-timeout kill, then immediate re-run), and the report or
        # backup of the run being retried is exactly what must survive.
        up = root / "run.json"
        first = unique_path(up)
        first.write_text("first", encoding="utf-8")
        second = unique_path(up)
        second.write_text("second", encoding="utf-8")
        third = unique_path(up)
        third.write_text("third", encoding="utf-8")
        t.check("unique_path leaves a free name alone", first == up)
        t.check("unique_path suffixes the second write", second != up and second.is_file())
        t.check(
            "unique_path keeps the first write readable",
            first.read_text(encoding="utf-8") == "first",
        )
        t.check("unique_path suffixes past an occupied suffix", third != second)
        # Suffix on the STEM, never inside the extension: report consumers and
        # tailers match on *.json.
        t.check("unique_path preserves the extension", second.suffix == ".json")
        for extra in (first, second, third):
            extra.unlink()

        # 8b. A write that fails AFTER the temp file exists must not strand it:
        # a directory at the target path makes os.replace fail (EISDIR) with the
        # temp already on disk, and litter beside the live config survives every
        # later run. The failure itself must still surface, not be swallowed.
        wa_dir = root / "unreplaceable"
        wa_dir.mkdir()
        try:
            write_atomic(wa_dir, '{"k": 3}\n')
            replace_failed = False
        except OSError:
            replace_failed = True
        t.check("failed atomic write raises", replace_failed)
        t.check(
            "failed atomic write leaves no temp files",
            [p.name for p in root.iterdir() if ".tmp" in p.name] == [],
        )

        # 8d. A write that fails while WRAPPING the descriptor must not strand
        # the raw fd either. os.fdopen takes ownership only once it RETURNS, so
        # an exception out of it leaves the descriptor open for the rest of the
        # process while the unlink below removes the name it was opened under:
        # an inode with no name and no way left to close it. Counted through
        # /proc/self/fd, the same host-only walk harness_common uses.
        real_fdopen = os.fdopen

        def wrap_boom(*_args: object, **_kwargs: object) -> NoReturn:
            msg = "descriptor wrap failed"
            raise OSError(msg)

        fd_dir = Path("/proc/self/fd")
        fds_open = sum(1 for _ in fd_dir.iterdir())
        os.fdopen = wrap_boom
        try:
            write_atomic(root / "fdleak.json", '{"k": 4}\n')
            wrap_failed = False
        except OSError:
            wrap_failed = True
        finally:
            os.fdopen = real_fdopen
        t.check("a failed descriptor wrap raises", wrap_failed)
        t.check(
            "a failed descriptor wrap leaks no file descriptor",
            sum(1 for _ in fd_dir.iterdir()) <= fds_open,
        )
        t.check(
            "a failed descriptor wrap leaves no temp files",
            [p.name for p in root.iterdir() if ".tmp" in p.name] == [],
        )

    # 9-11 exercise recover()/begin() against damaged states; they use their
    # own scratch dir so the numbering above keeps its end state.
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        cfg = root / "efficientserver.json"
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")

        # 9. UNREADABLE backup (crash mid-write of the snapshot itself):
        # quarantine it as evidence and leave the live file untouched - a
        # restore can never be attempted from bytes nobody can parse.
        s6 = mk()
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
        s6.bak.write_text("{ truncated", encoding="utf-8")
        live_before = cfg.read_bytes()
        s6.recover()
        stale6 = s6.bak.with_suffix(s6.bak.suffix + STALE_SUFFIX)
        t.check("unreadable backup quarantined", stale6.is_file() and not s6.bak.exists())
        t.check("unreadable backup leaves live untouched", cfg.read_bytes() == live_before)
        stale6.unlink()

        # 10. backup exists but the LIVE config is missing at recover time:
        # the divergence rule cannot apply (nothing to compare), so this is
        # quarantined too - restoring into a missing path would resurrect a
        # config an operator may have deleted deliberately.
        s7 = mk()
        s7.bak.write_text(json.dumps(original, indent=2), encoding="utf-8")
        cfg.unlink()
        s7.recover()
        stale7 = s7.bak.with_suffix(s7.bak.suffix + STALE_SUFFIX)
        t.check(
            "missing live config at recover -> backup quarantined",
            stale7.is_file() and not s7.bak.exists(),
        )
        t.check("missing live config stays missing after recover", not cfg.exists())
        stale7.unlink()

        # 11. begin() on a missing live config must fail loudly (named error)
        # instead of snapshotting nothing and letting restore() "succeed" by
        # writing an empty managed-keys-only file later.
        try:
            mk().begin()
            begin_raised_named_error = False
        except FileNotFoundError:
            begin_raised_named_error = True
        t.check("begin on missing live config fails loudly", begin_raised_named_error)

        # 12. live config is VALID JSON but not an object at recover time:
        # parses fine, so the unreadable branch does not fire, yet the
        # divergence rule cannot apply (no keys to replay into). Must be
        # quarantined like every other damaged state, never a crash.
        for shape in ("[1, 2]", "null", '"text"', "42"):
            s8 = mk()
            cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
            s8.begin()
            cfg.write_text(shape, encoding="utf-8")
            try:
                s8.recover()
                recovered_cleanly = True
            except Exception:
                recovered_cleanly = False
            stale8 = s8.bak.with_suffix(s8.bak.suffix + STALE_SUFFIX)
            t.check(
                f"non-object live config ({shape}) quarantined without raising",
                recovered_cleanly and stale8.is_file() and not s8.bak.exists(),
            )
            stale8.unlink()

        # 13. non-object live config at RESTORE time takes the full-backup
        # branch (a key-scoped rebuild would destroy or misplace operator
        # settings), consuming the backup exactly like the corrupt-live case.
        s9 = mk()
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
        s9.begin()
        snapshot = _canonical(_read_doc(cfg))
        cfg.write_text("[1]", encoding="utf-8")
        s9.restore()
        t.check(
            "non-object live config restored from full backup",
            _canonical(_read_doc(cfg)) == snapshot,
        )
        t.check("backup consumed after non-object-live restore", not s9.bak.exists())

        # 14. Atomic-write temps stranded by KILLED runs are swept: each one
        # is a file the protocol would never match again, so without the sweep
        # the live install directory grows one per killed run, forever. Temps
        # owned by a RUNNING pid (a concurrent harness) must survive, and the
        # live config and the guard's own backup are both covered.
        s10 = mk()
        dead_pid = 2**22 - 1  # above the default pid_max: cannot be running
        stray_cfg = root / f"{cfg.name}{TEMP_INFIX}{dead_pid}"
        stray_bak = root / f"{s10.bak.name}{TEMP_INFIX}{dead_pid}"
        # The parent's pid, not ours: a temp named with OUR pid is this very
        # run's write_atomic temp, which the snapshot below consumes by rename.
        live_tmp = root / f"{cfg.name}{TEMP_INFIX}{os.getppid()}"
        not_a_temp = root / f"{cfg.name}{TEMP_INFIX}notes.txt"
        for stray in (stray_cfg, stray_bak, live_tmp, not_a_temp):
            stray.write_text("{}", encoding="utf-8")
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
        s10.begin()
        t.check("temp of a dead run beside the config is swept", not stray_cfg.exists())
        t.check("temp of a dead run beside the backup is swept", not stray_bak.exists())
        t.check("temp owned by a live pid is kept", live_tmp.is_file())
        t.check("unrelated .tmp-named file is left alone", not_a_temp.is_file())
        s10.restore()
        live_tmp.unlink()
        not_a_temp.unlink()

        # 14b. The temp name carries an attempt counter after the pid, so the
        # sweep must read the pid off the front of the name and not treat the
        # whole suffix as one unparseable token.
        s10b = mk()
        dead_numbered = root / f"{cfg.name}{TEMP_INFIX}{dead_pid}_3"
        live_numbered = root / f"{cfg.name}{TEMP_INFIX}{os.getppid()}_3"
        for stray in (dead_numbered, live_numbered):
            stray.write_text("{}", encoding="utf-8")
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
        s10b.begin()
        t.check("counter-suffixed temp of a dead run is swept", not dead_numbered.exists())
        t.check("counter-suffixed temp of a live pid is kept", live_numbered.is_file())
        s10b.restore()
        live_numbered.unlink()

        # 15. TWO CONCURRENT RUNS on one install. The backup used to be a single
        # fixed path, so both runs snapshotted into and restored from the SAME
        # file: B's begin() read A's backup, saw the live config diverging only
        # on managed keys, concluded A had died, replayed A's snapshot over the
        # live config and unlinked the backup A still needed; A's restore then
        # replayed B's snapshot over the operator's file. Both interleavings
        # need no kill, only two harnesses.
        #
        # A second run is simulated as a ConfigSwap whose backup name carries a
        # DIFFERENT (live) pid, which is exactly what a second process produces
        # now that backups are pid-scoped. Its owner must be left strictly
        # alone: not deleted, not replayed, not quarantined.
        other_pid = os.getppid()
        if other_pid == os.getpid():  # pragma: no cover - defensive
            other_pid = os.getpid() - 1
        run_a = mk()
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
        run_a.begin()
        run_b_bak = cfg.with_name(f"{cfg.name}{BAK_INFIX}{other_pid}")
        t.check(
            "two runs on one install name two different backups",
            run_b_bak != run_a.bak and _pid_alive(other_pid),
        )
        # B is mid-experiment: its backup holds a document B is about to
        # restore, and it has a managed key set to something A must not touch.
        b_snapshot = dict(original, Enabled=False)
        run_b_bak.write_text(json.dumps(b_snapshot, indent=2) + "\n", encoding="utf-8")
        # A mutates the live config, then a THIRD run (standing in for B's
        # begin(), which runs recover() first) executes underneath it. That
        # recovery must not consume A's or B's in-flight backups: both owners
        # are live, so both are skipped.
        a_live = json.loads(cfg.read_text(encoding=CFG_ENCODING))
        section(a_live, "Pathfinding")["MaxPathEnqueuesPerTick"] = 64
        cfg.write_text(json.dumps(a_live, indent=2) + "\n", encoding="utf-8")
        run_c = mk()
        # Two ConfigSwap instances in ONE process derive the same backup name,
        # so point this one at its own (absent) live-pid name: it then has no
        # backup of its own to resolve and can only reach the directory scan,
        # which is the behavior under test.
        run_c.bak = cfg.with_name(f"{cfg.name}{BAK_INFIX}{other_pid + 1}")
        run_c.recover()
        t.check(
            "a live run's backup survives another run's recovery",
            run_b_bak.is_file(),
        )
        t.check(
            "a live run's backup is never replayed over the live config",
            _read_doc(run_b_bak)["Enabled"] is False,
        )
        t.check(
            "another run's recovery leaves this run's backup alone",
            run_a.bak.is_file() and run_c.bak != run_b_bak,
        )
        run_a.restore()
        t.check(
            "this run still restores its own snapshot after a concurrent recovery",
            _canonical(_read_doc(cfg)) == _canonical(original),
        )
        run_b_bak.unlink()

        # 15b. A KILLED run's backup (a dead pid) is still recovered, which is
        # the whole point of scoping backups by owner: the recovery path must
        # find a backup that is not this run's.
        killed = cfg.with_name(f"{cfg.name}{BAK_INFIX}{dead_pid}")
        killed.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
        killed_live = dict(original, Enabled=False)
        cfg.write_text(json.dumps(killed_live, indent=2) + "\n", encoding="utf-8")
        mk().recover()
        t.check(
            "a dead run's backup is recovered by the next run",
            _canonical(_read_doc(cfg)) == _canonical(original) and not killed.exists(),
        )

        # 15c. A quarantined backup (the .stale evidence a prior run set aside)
        # is not itself an orphan to recover: its name matches the backup glob
        # but carries no owner. Re-processing it would consume the evidence and
        # quarantine it again on every later run.
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
        evidence = cfg.with_name(f"{cfg.name}{BAK_INFIX}{dead_pid}{STALE_SUFFIX}")
        evidence.write_text("{ not json ][", encoding="utf-8")
        mk().recover()
        t.check("quarantined evidence is left in place", evidence.is_file())

        # 14d. The pid comes off a NAME in the live install directory, not off
        # one this process minted, so the sweep has to reject a name it cannot
        # turn into a pid instead of raising out of begin(). str.isdigit()
        # accepts "²" (int() then raises ValueError) and accepts an unbounded
        # digit run (os.kill then raises OverflowError); neither exception is an
        # OSError, so either one propagated and killed the run mid-protocol,
        # with the config already snapshotted and not yet restored. 20 nines is
        # the OverflowError case: past the C int os.kill takes, and short enough
        # to stay inside NAME_MAX so the fixture is a real file on disk. Each
        # hostile name here is also one the sweep must leave in place: it is not
        # a temp this protocol can attribute to any process.
        s10d = mk()
        hostile = [
            root / f"{cfg.name}{TEMP_INFIX}\N{SUPERSCRIPT TWO}_0",
            root / f"{cfg.name}{TEMP_INFIX}{'9' * 20}_0",
            root / f"{cfg.name}{TEMP_INFIX}{_MAX_OWNER_PID + 1}_0",
            root / f"{cfg.name}{TEMP_INFIX}-1_0",
        ]
        for stray in hostile:
            stray.write_text("{}", encoding="utf-8")
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
        swept = True
        try:
            s10d.begin()
        except Exception as ex:
            # The failure this pins: any exception at all here, whatever its
            # type, must not escape the sweep.
            swept = False
            print(f"  sweep raised on a hostile temp name: {ex!r}", file=sys.stderr)
        t.check("a temp name that is not a pid does not abort the sweep", swept)
        t.check("a sweep that raised still snapshotted the config", s10d.bak.is_file())
        s10d.restore()
        t.check(
            "every unparseable temp name is left on disk", all(stray.is_file() for stray in hostile)
        )
        for stray in hostile:
            stray.unlink()

        # 14c. The temp name is predictable, so anything else on the host can
        # squat it first. A pre-planted symlink must not redirect the write
        # (O_EXCL refuses the name and the next attempt takes it), and the
        # squatting name itself is not this call's to delete.
        s10c = mk()
        s10c.recover()
        victim = root / "victim.json"
        victim.write_text("untouched", encoding="utf-8")
        squat = root / f"{cfg.name}{TEMP_INFIX}{os.getpid()}_0"
        squat.symlink_to(victim)
        write_atomic(cfg, json.dumps(original, indent=2) + "\n")
        t.check(
            "a pre-planted symlink at the temp name is not followed",
            victim.read_text(encoding="utf-8") == "untouched",
        )
        t.check(
            "the write lands on the live file anyway",
            _canonical(_read_doc(cfg)) == _canonical(original),
        )
        t.check("the squatting entry is left in place", squat.is_symlink())
        squat.unlink()

        # 15. TWO QUARANTINES must both leave evidence. A bench host that keeps
        # hitting a damaged config quarantines on every run, and a fixed
        # .stale name made run N+1's rename destroy run N's evidence - the one
        # artifact the quarantine exists to preserve.
        s11 = mk()
        s11.bak.write_text("{ truncated", encoding="utf-8")
        s11.recover()
        stales_a = sorted(p.name for p in root.glob(s11.bak.name + "*" + STALE_SUFFIX))
        s11.bak.write_text("{ also truncated", encoding="utf-8")
        s11.recover()
        stales_b = sorted(p.name for p in root.glob(s11.bak.name + "*" + STALE_SUFFIX))
        t.check(
            "second quarantine keeps the first's evidence",
            len(stales_a) == 1 and len(stales_b) == 2 and stales_a[0] in stales_b,
        )
        t.check("second quarantine consumed its own backup", not s11.bak.exists())

        # The bound: the suffix above makes the sequence grow without limit, and
        # a bench host that keeps hitting a damaged config quarantines on every
        # run, so the LIVE install directory accumulated one file per killed run
        # forever (and install.sh preserves them all on every reinstall). Only the
        # newest few describe a state any later run can be compared against.
        s12 = mk()
        for i in range(STALE_KEEP + 3):
            s12.bak.write_text(f"{{ truncated {i}", encoding="utf-8")
            s12.recover()
        kept = sorted(p.name for p in root.glob(s12.bak.name + "*" + STALE_SUFFIX))
        bodies = {(root / name).read_text(encoding="utf-8") for name in kept}
        t.check(
            "quarantines are bounded, not one per run forever",
            len(kept) == STALE_KEEP,
        )
        # Newest kept, oldest dropped: the operator's evidence after a burst of
        # kills is the recent one, and a count bound that kept the OLDEST
        # would satisfy the length check above while being useless.
        last = STALE_KEEP + 2
        t.check(
            "the newest quarantines are the ones kept",
            f"{{ truncated {last}" in bodies and "{ truncated 0" not in bodies,
        )
        t.check("the bounded prune consumed its own backup", not s12.bak.exists())
        # Each run's backup carries its own pid, so quarantines from earlier
        # (dead) runs have other names and still count against the bound.
        for i in range(STALE_KEEP):
            (root / f"{s12.cfg.name}{BAK_INFIX}{dead_pid + i}{STALE_SUFFIX}").write_text(
                "{ old", encoding="utf-8"
            )
        s12.bak.write_text("{ truncated again", encoding="utf-8")
        s12.recover()
        all_kept = list(root.glob(f"{s12.cfg.name}{BAK_INFIX}*{STALE_SUFFIX}"))
        t.check(
            "quarantines from other runs' backups share one bound",
            len(all_kept) == STALE_KEEP,
        )
        for path in all_kept:
            path.unlink()

    # 16. THE WHOLE PROTOCOL, RUN TWICE, MUST CONVERGE. Everything above
    # exercises one damaged state at a time; this is the property the module
    # actually promises - a second identical run leaves the config exactly as
    # one run does, with no backup, no stale file and no temp litter left
    # behind to change the next run's behavior.
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        cfg = root / "efficientserver.json"
        seeded = json.dumps(original, indent=2) + "\n"
        cfg.write_text(seeded, encoding="utf-8")

        def one_run() -> None:
            s = ConfigSwap(cfg, keys, log=lambda _m: None)
            s.begin()
            doc = _read_doc(cfg)
            doc["Enabled"] = False
            section(doc, "Pathfinding")["MaxPathEnqueuesPerTick"] = 64
            cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
            s.restore()

        one_run()
        after_one = cfg.read_bytes()
        one_run()
        after_two = cfg.read_bytes()
        t.check("running the protocol twice converges byte-for-byte", after_one == after_two)
        t.check("a completed protocol leaves the config intact", after_two == seeded.encode())
        t.check(
            "a completed protocol leaves no backup, stale or temp",
            sorted(p.name for p in root.iterdir()) == [cfg.name],
        )

    # 17. A snapshot that is valid JSON but NOT an object has no managed key to
    # read back. The key-scoped walk used to read every managed key as absent
    # and write the live config without it, which is the opposite of a restore:
    # it silently dropped Enabled and two Pathfinding knobs off the live file.
    # The full-snapshot exit is the only safe answer, same as a damaged live
    # document.
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        cfg = root / "efficientserver.json"
        cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
        for shape in ("[1, 2]", "null", '"text"', "42"):
            cfg.write_text(json.dumps(original, indent=2) + "\n", encoding="utf-8")
            s = ConfigSwap(cfg, keys, log=logs.append)
            s.bak.write_text(shape, encoding="utf-8")
            s.restore()
            t.check(
                f"non-object snapshot ({shape}) restored verbatim, not key-scoped",
                cfg.read_text(encoding="utf-8") == shape and not s.bak.exists(),
            )

    # 18. The same protocol under randomized hostile input: the fixtures above
    # pin one damaged state each, this drives random combinations of them.
    failures: list[str] = []
    for iteration in range(_FUZZ_ITERATIONS):
        _fuzz_protocol(failures, iteration)
    for detail in failures[:10]:
        print("FAIL: fuzz: " + detail, file=sys.stderr)
    t.check(
        f"fuzz: {_FUZZ_ITERATIONS} randomized protocol cases on hostile configs"
        f" ({len(failures)} invariant violation(s))",
        not failures,
    )

    return t.finish()


if __name__ == "__main__":
    run_cli("scripts/es_cfg_guard.py", USAGE, _selftest, _selftest)
