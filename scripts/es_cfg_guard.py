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
    * Otherwise the live file moved on since (operator edit, reinstall): the
      backup is stale evidence. Rename it to ``<backup>.stale`` and touch
      nothing - never restore across unrelated edits.
- ``begin()``: ``recover()``, then snapshot the live file byte-exact.
  Safe to call again while already begun (no re-snapshot).
- ``restore()``: write ONLY the managed keys (absence included) from the
  snapshot back into the live file, then delete it. Other keys keep whatever
  is on disk now, so a restore cannot clobber newer operator tuning. A second
  call is a no-op.

All writes go through temp-file + rename so a kill mid-write cannot leave a
truncated JSON behind for the game's config reader or the next run.
"""
from __future__ import annotations

import json
import os
import shutil
import sys
from pathlib import Path

STALE_SUFFIX = ".stale"

USAGE = """\
usage: es_cfg_guard.py [--selftest] [-h | --help]

Backup/restore guard library for the installed EfficientServer config
(imported by the bench harnesses), plus a self-test of that protocol.
  --selftest  run the protocol self-test (default with no arguments)
  -h, --help  show this help\
"""


def _read_doc(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _canonical(doc: dict) -> str:
    return json.dumps(doc, sort_keys=True, separators=(",", ":"))


def _write_atomic(path: Path, data: bytes) -> None:
    tmp = path.with_name(path.name + f".tmp{os.getpid()}")
    tmp.write_bytes(data)
    os.replace(tmp, path)


class ConfigSwap:
    """Backup-modify-restore for selected keys of one JSON config file."""

    def __init__(self, cfg_path: Path, keys: list[tuple[str, ...]], log=print):
        self.cfg = cfg_path
        self.bak = cfg_path.with_suffix(cfg_path.suffix + ".swap-bak")
        # Accept bare "Key" strings so a missed trailing comma cannot turn a
        # key path into character-wise iteration.
        self.keys = [k if isinstance(k, tuple) else (k,) for k in keys]
        self._log = log
        self._begun = False

    # -- key helpers -------------------------------------------------------

    def _get(self, doc: dict, kp: tuple[str, ...]) -> tuple[bool, object]:
        node = doc
        for k in kp[:-1]:
            if not isinstance(node, dict) or k not in node:
                return False, None
            node = node[k]
        if not isinstance(node, dict) or kp[-1] not in node:
            return False, None
        return True, node[kp[-1]]

    def _set(self, doc: dict, kp: tuple[str, ...], present: bool, value: object) -> None:
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

    def _quarantine(self, why: str) -> None:
        stale = self.bak.with_suffix(self.bak.suffix + STALE_SUFFIX)
        os.replace(self.bak, stale)
        self._log(
            f"config guard: leftover backup {self.bak.name} is stale ({why}); "
            f"kept as evidence at {stale.name}, live file NOT touched"
        )

    def recover(self) -> None:
        """Resolve a backup left behind by an earlier killed run."""
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
            live = _read_doc(self.cfg)
        except Exception as e:
            self._quarantine(f"live config unreadable: {e}")
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
            shutil.copy2(self.bak, self.cfg)
            self.bak.unlink()
            self._log("config guard: live config was missing; restored full backup")
            return
        try:
            live = _read_doc(self.cfg)
        except Exception as e:
            # Unreadable live JSON: rebuilding from {} would write back ONLY the
            # managed keys and destroy every other operator setting, so fall
            # back to the exact snapshot instead.
            shutil.copy2(self.bak, self.cfg)
            self.bak.unlink()
            self._log(
                f"config guard: live config unreadable ({e}); "
                "restored full backup"
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

    failures: list[str] = []

    def check(name: str, cond: bool) -> None:
        if cond:
            print("PASS: " + name)
        else:
            print("FAIL: " + name, file=sys.stderr)
            failures.append(name)

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
        doc["Pathfinding"]["MaxPathEnqueuesPerTick"] = 64
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        s.restore()
        expected = json.dumps(original, indent=2).encode() + b"\n"
        check("roundtrip restores exact bytes", cfg.read_bytes() == expected)

        # 2. crash simulation: a NEW instance finishes the interrupted restore.
        s = mk()
        s.begin()
        doc = _read_doc(cfg)
        doc["Enabled"] = False
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        mk().recover()  # next run, before any mutation
        check("crashed run recovered", _canonical(_read_doc(cfg)) == _canonical(original))
        check("recovery consumed backup", not s.bak.exists())

        # 3. stale backup: live diverged beyond managed keys -> untouched.
        s.begin()
        doc = _read_doc(cfg)
        doc["Network"]["EntityDistributionEveryTicks"] = 3  # operator edit
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        mk().recover()
        stale = s.bak.with_suffix(s.bak.suffix + STALE_SUFFIX)
        check("stale quarantined", stale.is_file() and not s.bak.exists())
        check(
            "stale recover leaves live untouched",
            _read_doc(cfg)["Network"]["EntityDistributionEveryTicks"] == 3,
        )

        # 4. restore is key-scoped and repeat-safe; absence is preserved too.
        s2 = mk()
        s2.begin()  # snapshots the operator-edited file
        stale.unlink()
        doc = _read_doc(cfg)
        doc["Enabled"] = False
        doc["Pathfinding"]["DropPathWhenFarDistSq"] = 2500  # key absent in snapshot
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        s2.restore()
        s2.restore()  # second call must be a no-op
        after = _read_doc(cfg)
        check(
            "key-scoped restore keeps other keys",
            after["Network"]["EntityDistributionEveryTicks"] == 3,
        )
        check("restore reverts managed key", after["Enabled"] is True)
        check(
            "restore removes key absent in snapshot",
            "DropPathWhenFarDistSq" not in after["Pathfinding"],
        )
        check("repeat restore is a no-op", not s2.bak.exists())

        # 5. double begin does not re-snapshot over modified state.
        s3 = mk()
        s3.begin()
        doc = _read_doc(cfg)
        doc["Enabled"] = False
        cfg.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
        s3.begin()
        s3.restore()
        check("begin is sticky until restored", _read_doc(cfg)["Enabled"] is True)

        # 6. live file deleted between begin and restore -> full backup restore
        # (not just managed keys, which would produce a truncated config).
        s4 = mk()
        s4.begin()
        snapshot = _canonical(_read_doc(cfg))
        cfg.unlink()
        s4.restore()
        check(
            "missing live config restored from full backup",
            cfg.is_file() and _canonical(_read_doc(cfg)) == snapshot,
        )
        check("backup consumed after missing-live restore", not s4.bak.exists())

        # 7. live file UNREADABLE (corrupt JSON) between begin and restore ->
        # full backup restore too: key-scoped restore onto {} would silently
        # drop every non-managed operator key.
        s5 = mk()
        s5.begin()
        snapshot = _canonical(_read_doc(cfg))
        cfg.write_text("{ this is not json ][", encoding="utf-8")
        s5.restore()
        check(
            "corrupt live config restored from full backup",
            _canonical(_read_doc(cfg)) == snapshot,
        )
        check("backup consumed after corrupt-live restore", not s5.bak.exists())

    if failures:
        print(f"FAIL: {len(failures)} es_cfg_guard selftest check(s)", file=sys.stderr)
        return 1
    print("PASS: es_cfg_guard selftest")
    return 0


if __name__ == "__main__":
    argv = sys.argv[1:]
    if argv in (["-h"], ["--help"]):
        print(USAGE)
        raise SystemExit(0)
    if argv and argv != ["--selftest"]:
        print(f"es_cfg_guard.py: unrecognized arguments: {' '.join(argv)}", file=sys.stderr)
        print(USAGE, file=sys.stderr)
        raise SystemExit(2)
    raise SystemExit(_selftest())
