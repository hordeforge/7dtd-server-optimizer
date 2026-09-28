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

All writes go through temp-file + rename so a kill mid-write cannot leave a
truncated JSON behind for the game's config reader or the next run.
"""
from __future__ import annotations

import json
import os
from collections.abc import Callable, Sequence
from pathlib import Path

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
# temp stranded by a killed run is attributable to that run.
TEMP_INFIX = ".tmp"

USAGE = """\
usage: es_cfg_guard.py [--selftest] [-h | --help]

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


def _canonical(doc: dict[str, object]) -> str:
    return json.dumps(doc, sort_keys=True, separators=(",", ":"))


def _write_atomic(path: Path, data: bytes) -> None:
    tmp = path.with_name(path.name + f"{TEMP_INFIX}{os.getpid()}")
    try:
        tmp.write_bytes(data)
        os.replace(tmp, path)
    except BaseException:
        # The temp file is this call's only copy of the data until the rename
        # lands, and a failed write (ENOSPC, EACCES on the target directory) or
        # a failed replace (cross-device, target is a directory, read-only fs)
        # leaves it stranded next to the live config for every later run to
        # trip over. Drop it, then let the original error propagate unchanged.
        try:
            tmp.unlink()
        except OSError:
            pass
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
        self.bak = cfg_path.with_suffix(cfg_path.suffix + ".swap-bak")
        self.keys = keys
        self._log = log
        self._begun = False

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

        ``write_atomic`` names its temp ``<file>.tmp<pid>`` and renames it into
        place, so a run SIGKILLed (or power-cut) between the write and the
        rename leaves one file per killed run in the LIVE install directory,
        under a name nothing in the protocol ever matches again. These runs are
        routinely killed by tool timeouts, so a bench host accumulates them
        without bound. A temp whose owning pid is gone cannot be an in-flight
        write, so it is garbage; one whose pid is still running (a concurrent
        harness, or a recycled pid) is left alone.
        """
        for base in (self.cfg, self.bak):
            for tmp in base.parent.glob(f"{base.name}{TEMP_INFIX}*"):
                owner = tmp.name[len(base.name) + len(TEMP_INFIX) :]
                if not owner.isdigit():
                    continue
                try:
                    os.kill(int(owner), 0)
                except ProcessLookupError:
                    tmp.unlink(missing_ok=True)
                except OSError:
                    pass  # alive (or not ours to signal): not ours to remove

    def _quarantine(self, why: str) -> None:
        # Suffix-resolved: two quarantines in the same run's lifetime (or two
        # runs of a bench loop that keeps hitting damaged state) must not have
        # the second rename destroy the first one's evidence.
        stale = unique_path(self.bak.with_suffix(self.bak.suffix + STALE_SUFFIX))
        os.replace(self.bak, stale)
        self._log(
            f"config guard: leftover backup {self.bak.name} is stale ({why}); "
            f"kept as evidence at {stale.name}, live file NOT touched"
        )

    def recover(self) -> None:
        """Resolve a backup left behind by an earlier killed run."""
        self._sweep_abandoned_temps()
        if not self.bak.is_file():
            return
        try:
            bak_doc = _read_doc(self.bak)
            bak_bytes = self.bak.read_bytes()
        except Exception as e:
            self._quarantine(f"unreadable: {e}")
            return
        if not self.cfg.is_file():
            self._quarantine("live config missing")
            return
        try:
            # Not _read_doc: the live document's SHAPE is unknown here (that is
            # what the check below decides), so parse untyped like the boundary
            # json.loads is.
            live: object = json.loads(self.cfg.read_text(encoding=CFG_ENCODING))
        except Exception as e:
            self._quarantine(f"live config unreadable: {e}")
            return
        # A VALID-JSON but non-object live document (array, scalar, null) parses
        # yet has no keys to replay managed values into: the divergence rule
        # cannot apply, so quarantine like any other damaged state - restoring
        # across a shape an operator (or a corrupting writer) produced is never
        # this guard's call.
        if not isinstance(live, dict):
            self._quarantine(
                f"live config is valid JSON but not an object ({type(live).__name__})"
            )
            return
        # Replay the backup's managed-key values onto the live doc; if that
        # makes the documents identical, only this harness touched the file
        # since the snapshot -> finish the interrupted restore.
        replayed = json.loads(json.dumps(live))
        for kp in self.keys:
            present, value = self._get(bak_doc, kp)
            self._set(replayed, kp, present, value)
        if _canonical(replayed) == _canonical(bak_doc):
            _write_atomic(self.cfg, bak_bytes)
            self.bak.unlink()
            self._log(
                "config guard: finished restore from backup left by a "
                "killed earlier run"
            )
        else:
            self._quarantine("live config changed beyond managed keys since snapshot")

    def begin(self) -> None:
        """Snapshot the live file for later restore (idempotent once begun)."""
        self._sweep_abandoned_temps()
        if self._begun and self.bak.is_file():
            return
        # Resolve any backup a killed earlier run left before snapshotting.
        self.recover()
        if not self.cfg.is_file():
            raise FileNotFoundError(f"missing {self.cfg}")
        _write_atomic(self.bak, self.cfg.read_bytes())
        self._begun = True
        self._log(f"config guard: snapshotted {self.cfg.name} -> {self.bak.name}")

    def restore(self) -> None:
        """Put the managed keys back to their snapshotted values (idempotent)."""
        if not self.bak.is_file():
            return
        try:
            bak_doc = _read_doc(self.bak)
        except Exception as e:
            self._log(f"config guard: RESTORE FAILED, backup kept ({e})")
            return
        if not self.cfg.is_file():
            _write_atomic(self.cfg, self.bak.read_bytes())
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
            _write_atomic(self.cfg, self.bak.read_bytes())
            self.bak.unlink()
            self._log(
                f"config guard: live config unreadable ({e}); "
                "restored full backup"
            )
            return
        if not isinstance(live, dict):
            # Valid JSON of the wrong shape (array, scalar, null): a key-scoped
            # rebuild would likewise destroy or misplace every operator setting,
            # so take the same full-snapshot exit as the unreadable case.
            _write_atomic(self.cfg, self.bak.read_bytes())
            self.bak.unlink()
            self._log(
                f"config guard: live config is valid JSON but not an object "
                f"({type(live).__name__}); restored full backup"
            )
            return
        for kp in self.keys:
            present, value = self._get(bak_doc, kp)
            self._set(live, kp, present, value)
        _write_atomic(
            self.cfg, (json.dumps(live, indent=2) + "\n").encode("utf-8")
        )
        self.bak.unlink()
        self._begun = False
        self._log(f"config guard: restored managed keys from {self.bak.name}")


def _selftest() -> int:
    import tempfile

    checks = Checks("es_cfg_guard")

    t = checks

    def section(doc: dict[str, object], key: str) -> dict[str, object]:
        """Narrow a top-level JSON object to one of its nested sections."""
        sub = doc[key]
        if not isinstance(sub, dict):
            raise TypeError(f"config section {key!r} is not a JSON object")
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
        bom_bak = bom_cfg.with_suffix(bom_cfg.suffix + ".swap-bak")
        bom_bak.write_bytes(bom_cfg.read_bytes())
        mk_bom = ConfigSwap(bom_cfg, [("Enabled",)], log=logs.append)
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
        t.check("missing live config at recover -> backup quarantined",
              stale7.is_file() and not s7.bak.exists())
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
        for shape in ('[1, 2]', 'null', '"text"', '42'):
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
        dead_pid = 2 ** 22 - 1  # above the default pid_max: cannot be running
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
        for name in stales_b:
            (root / name).unlink()

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

    return checks.finish()


if __name__ == "__main__":
    run_cli("es_cfg_guard.py", USAGE, _selftest, _selftest)
