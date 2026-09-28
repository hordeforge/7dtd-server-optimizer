#!/usr/bin/env python3
"""Off-host snapshot, verification and restore of the live EfficientServer config.

The installed `Mods/EfficientServer/Config/efficientserver.json` is the only copy
of an operator's tuning: it is edited on the server host, never in this repo, and
nothing else regenerates it. `install.sh` and `uninstall.sh` preserve it, but both
copies live inside the install tree, so a lost disk or a lost instance takes the
backup with the data. A copy the same disk can lose is not a backup.

This tool moves the live config to a destination the operator names (another
disk, a synced folder, a host off this one) and, in the same pass, proves the copy
is loadable: the snapshot is parsed, its keys are checked against the shipped
template, and its sha256 is recorded in a manifest that `verify` re-checks later.
A backup nobody has read back is a hypothesis.

    python3 scripts/backup_config.py --dest /mnt/backup/es-config
    python3 scripts/backup_config.py --dest /mnt/backup/es-config --verify
    python3 scripts/backup_config.py --dest /mnt/backup/es-config \
        --restore 20260928_101500 --to ./recovered.json

`--verify` is the restore drill's cheap half: it reads every snapshot back the way
a restore would and exits nonzero on the first one that would not load. Run it on
a schedule; a backup whose failure is silent is not a backup.

    python3 scripts/backup_config.py --selftest    (wired into `make test`)
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import TypedDict

from repo_root import repo_root
from selftest_support import Checks

ROOT = repo_root()
TEMPLATE_JSON = ROOT / "config" / "efficientserver.json"

CONFIG_REL = Path("Mods/EfficientServer/Config/efficientserver.json")
MANIFEST_NAME = "manifest.json"
CONFIG_NAME = "efficientserver.json"
# Snapshots are small (one JSON file plus a manifest); a fortnight of daily
# copies costs nothing and bounds how far back a restore can reach.
DEFAULT_KEEP = 14
STAMP_FORMAT = "%Y%m%d_%H%M%S"
# A snapshot dir is the stamp, plus "_N" for the Nth copy taken inside that
# second. Both halves are parsed for ordering; see _snapshot_order.
_SNAPSHOT_NAME = re.compile(r"(\d{8}_\d{6})(?:_(\d+))?")
# Width of the same-second collision suffix, so directory names sort in
# creation order (see snapshot's suffix loop and snapshot_dirs).
STAMP_SUFFIX_DIGITS = 3

# The game's own reader (Config.Load) decodes the config as UTF-8 with a leading
# BOM tolerated, so a BOM is a legal config here too; utf-8-sig is a strict
# superset of utf-8. Same rule es_cfg_guard.py reads the file under.
CFG_ENCODING = "utf-8-sig"

USAGE = """\
Snapshot, verify and restore the live config
(Mods/EfficientServer/Config/efficientserver.json), the one piece of state an
operator edits on the server host that nothing else regenerates. The install
root comes from DS/SEVENDTD_DS_DIR.

  python3 scripts/backup_config.py --dest /mnt/backup/es-config
  python3 scripts/backup_config.py --dest /mnt/backup/es-config --verify
  python3 scripts/backup_config.py --dest /mnt/backup/es-config \\
      --restore 20260928_101500 --to ./recovered.json

--verify is the restore drill's cheap half: it reads every snapshot back the
way a restore would and exits nonzero on the first one that would not load. Run
it on a schedule; a backup whose failure is silent is not a backup.
"""


class BackupError(Exception):
    """An operator-actionable failure; printed without a traceback."""


class Manifest(TypedDict):
    stamp: str
    source: str
    sha256: str


def utc_stamp(now: datetime | None = None) -> str:
    """Second-resolution UTC name. Suffixing lives in the caller, not here."""
    return (now or datetime.now(timezone.utc)).strftime(STAMP_FORMAT)


def sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_config(path: Path) -> dict[str, object]:
    """Parse a config file, raising BackupError on anything unloadable.

    A non-object document (a list, a bare number) parses fine and still cannot
    be a config, so the shape is checked here rather than trusted downstream.
    """
    try:
        doc = json.loads(path.read_text(encoding=CFG_ENCODING))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        msg = f"unreadable config {path}: {exc}"
        raise BackupError(msg) from exc
    if not isinstance(doc, dict):
        msg = f"config {path} is a {type(doc).__name__}, not a JSON object"
        raise BackupError(msg)
    return doc


def template_keys(template: Path = TEMPLATE_JSON) -> frozenset[str]:
    """Top-level keys the shipped template declares; a snapshot may not add more.

    The mod's loader ignores unknown keys silently, so a typo'd key is a knob
    that quietly never applies. check_config_doc.py proves the template itself;
    this only bounds a snapshot against it.
    """
    return frozenset(read_config(template))


def unknown_keys(doc: dict[str, object], template: Path = TEMPLATE_JSON) -> set[str]:
    return set(doc) - template_keys(template)


def live_config(server_root: Path) -> Path:
    return server_root / CONFIG_REL


def _resolve_outside_install(dest: Path, server_root: Path) -> Path:
    """Fail when the snapshot destination sits inside the install tree.

    Preserving a copy next to the data it protects defeats the only disaster this
    tool exists for (instance or disk loss), so it is refused here instead of
    documented as a caveat the operator reads once and forgets.
    """
    dest_r = dest.resolve()
    srv_r = server_root.resolve()
    if dest_r == srv_r or srv_r in dest_r.parents:
        msg = (
            f"--dest {dest_r} is inside the server install tree {srv_r};"
            " a lost disk would take the backup with the config."
            " Point it at another disk, a synced directory or another host."
        )
        raise BackupError(msg)
    return dest_r


def snapshot(
    server_root: Path,
    dest: Path,
    *,
    now: datetime | None = None,
    keep: int = DEFAULT_KEEP,
) -> tuple[Path, list[str]]:
    """Copy the live config into a stamped snapshot dir; return it and any problems.

    The snapshot is written and then read back, so a successful return means the
    copy loads, not merely that `cp` exited 0. A live config that is missing or
    unloadable is an error: an empty snapshot dir would look like a healthy backup.
    """
    if keep < 1:
        msg = f"--keep must be at least 1, got {keep}"
        raise BackupError(msg)
    dest = _resolve_outside_install(dest, server_root)
    live = live_config(server_root)
    if not live.is_file():
        msg = (
            f"no live config at {live}; nothing to snapshot"
            " (install the mod with `make install DS=...` first)"
        )
        raise BackupError(msg)
    doc = read_config(live)
    stray = unknown_keys(doc)
    if stray:
        print(
            f"WARNING: {live} has keys the shipped template does not:"
            f" {', '.join(sorted(stray))}. They are preserved verbatim; the mod"
            " ignores unknown keys.",
            file=sys.stderr,
        )

    stamp = utc_stamp(now)
    target = dest / stamp
    n = 1
    while target.exists():
        # Zero-padded so snapshot_dirs' "the UTC stamp sorts chronologically"
        # holds for same-second snapshots too: bare `_2`, `_10` sort as
        # text, and prune would then keep an OLDER snapshot and delete a newer
        # one, which is the exact loss a rerun must not cause.
        target = dest / f"{stamp}_{n:0{STAMP_SUFFIX_DIGITS}d}"
        n += 1
    target.mkdir(parents=True)
    manifest: Manifest = {
        "stamp": target.name,
        "source": str(live),
        "sha256": sha256_of(live),
    }
    (target / CONFIG_NAME).write_bytes(live.read_bytes())
    (target / MANIFEST_NAME).write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )

    problems = verify(dest)
    if problems:
        raise BackupError(
            f"snapshot {target.name} does not verify: " + "; ".join(problems)
        )
    prune(dest, keep)
    return target, []


def _snapshot_order(name: str) -> tuple[str, int]:
    """Chronological order key: the stamp first, then the same-second counter.

    Sorting on the raw name is wrong once a second holds ten or more copies:
    `..._101500_10` orders before `..._101500_2` bytewise, so prune would treat
    the ninth copy as the oldest and delete the tenth instead. A name this tool
    did not write keeps its own name as the leading key so the order stays
    total.
    """
    m = _SNAPSHOT_NAME.fullmatch(name)
    if m is None:
        return (name, 0)
    return (m.group(1), int(m.group(2) or 0))


def snapshot_dirs(dest: Path) -> list[Path]:
    """Snapshot dirs, oldest first (see `_snapshot_order`)."""
    if not dest.is_dir():
        return []
    found = [
        p
        for p in dest.iterdir()
        if p.is_dir() and (p / CONFIG_NAME).is_file() and (p / MANIFEST_NAME).is_file()
    ]
    return sorted(found, key=lambda p: _snapshot_order(p.name))


def prune(dest: Path, keep: int) -> list[Path]:
    """Delete all but the `keep` newest snapshots; return the ones removed."""
    dirs = snapshot_dirs(dest)
    removed = dirs[:-keep] if keep < len(dirs) else []
    for old in removed:
        shutil.rmtree(old)
    return removed


def verify(dest: Path) -> list[str]:
    """Read every snapshot the way a restore would; return one string per failure.

    Checks the bytes against the manifest sha256, then parses the file and bounds
    its keys against the shipped template. An empty or missing dest is reported
    as a failure: "no backup exists" must never read as "backups are healthy".
    """
    dirs = snapshot_dirs(dest)
    if not dirs:
        return [f"no snapshots under {dest}"]
    problems: list[str] = []
    for d in dirs:
        manifest_path = d / MANIFEST_NAME
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            problems.append(f"{d.name}: unreadable manifest ({exc})")
            continue
        recorded = manifest.get("sha256") if isinstance(manifest, dict) else None
        actual = sha256_of(d / CONFIG_NAME)
        if recorded != actual:
            problems.append(f"{d.name}: sha256 {actual} != manifest {recorded}")
            continue
        try:
            doc = read_config(d / CONFIG_NAME)
        except BackupError as exc:
            problems.append(str(exc))
            continue
        stray = unknown_keys(doc)
        if stray:
            problems.append(f"{d.name}: keys not in the shipped template: {sorted(stray)}")
    return problems


def restore(dest: Path, stamp: str, to: Path, *, force: bool = False) -> Path:
    """Copy a verified snapshot to `to`. Refuses to clobber the live config silently.

    Restoring over the live config is allowed, but only when the caller says so
    explicitly: the live file is the state being recovered, and an unexpected
    overwrite of it is the one mistake a restore tool can make unrecoverable.
    """
    src = dest / stamp
    if not (src / CONFIG_NAME).is_file():
        available = ", ".join(p.name for p in snapshot_dirs(dest)) or "none"
        msg = f"no snapshot '{stamp}' under {dest} (have: {available})"
        raise BackupError(msg)
    problems = verify(dest)
    if any(p.startswith(f"{src.name}:") for p in problems):
        msg = f"snapshot {src.name} does not verify: " + "; ".join(problems)
        raise BackupError(msg)
    if to.exists() and not force:
        msg = f"{to} exists; pass --force to overwrite it"
        raise BackupError(msg)
    to.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src / CONFIG_NAME, to)
    return to


def _parse_args(argv: list[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(
        prog="backup_config.py",
        # Spelled out instead of derived: --selftest is the one invocation that
        # needs no --dest, and the derived line would show it as optional for
        # every other one.
        usage="backup_config.py [-h] --dest DIR [--verify]\n"
        "                 [--restore STAMP --to PATH] [--force] [--keep N]\n"
        "                 [--selftest]",
        description=USAGE,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument(
        "--dest",
        type=Path,
        metavar="DIR",
        help="where snapshots live (must be OFF this install tree, otherwise a "
        "lost disk takes the backup with the config)",
    )
    p.add_argument(
        "--verify",
        action="store_true",
        help="read every snapshot back and check it, exit 1 on any failure",
    )
    p.add_argument(
        "--restore",
        default=None,
        metavar="STAMP",
        help="restore one snapshot by its stamp; needs --to PATH",
    )
    p.add_argument(
        "--to",
        type=Path,
        default=None,
        metavar="PATH",
        help="restore target; copy the snapshot to PATH instead of over the live config",
    )
    p.add_argument("--force", action="store_true", help="let --to overwrite an existing file")
    p.add_argument(
        "--keep",
        type=int,
        default=DEFAULT_KEEP,
        metavar="N",
        help=f"snapshots to retain (default {DEFAULT_KEEP})",
    )
    p.add_argument(
        "--selftest",
        action="store_true",
        help="run the backup/restore self-test (wired into `make test`)",
    )
    args = p.parse_args(argv)
    # --selftest runs no backup work, so it must not be forced to name a
    # destination; every other invocation does. --selftest is exclusive, the
    # same contract the run_cli gates give it.
    if args.selftest:
        if argv != ["--selftest"]:
            p.error("--selftest takes no other arguments")
        return args
    if args.dest is None:
        p.error("--dest DIR is required")
    if args.restore and not args.to:
        p.error("--restore needs --to PATH (or copy it by hand from the printed command)")
    if args.restore and args.verify:
        p.error("--verify and --restore are separate operations")
    if args.force and not args.restore:
        p.error("--force only applies to --restore")
    # prune() deletes every snapshot outside the retained window, so a --keep
    # below 1 turns a typo into a wiped backup history.
    if args.keep < 1:
        p.error(f"--keep must be at least 1, got {args.keep}")
    return args


def default_server_root() -> Path:
    return Path(os.environ.get("SEVENDTD_DS_DIR") or os.environ.get("DS") or "").expanduser()


def _run(argv: list[str]) -> int:
    try:
        return _dispatch(_parse_args(argv))
    except BackupError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


def _dispatch(args: argparse.Namespace) -> int:
    if args.selftest:
        return _selftest()
    dest = args.dest
    if dest is None:
        # Unreachable: _parse_args rejects a missing --dest with a usage error
        # before dispatch. Present so the type is Path here, not Path | None.
        raise SystemExit(2)
    if args.verify:
        problems = verify(dest)
        for problem in problems:
            print(f"FAIL: {problem}", file=sys.stderr)
        if problems:
            return 1
        print(f"OK: {len(snapshot_dirs(dest))} snapshot(s) verify under {dest}")
        return 0
    if args.restore:
        out = restore(dest, args.restore, args.to, force=args.force)
        print(f"Restored {args.restore} -> {out}")
        print(f"  cp -a '{out}' '<DS>/Mods/EfficientServer/Config/{CONFIG_NAME}'")
        print("  es reload            # or restart the server")
        return 0
    server_root = default_server_root()
    if not str(server_root):
        msg = "no server install root: set SEVENDTD_DS_DIR (or DS) to the dedicated install"
        raise BackupError(msg)
    target, _ = snapshot(server_root, dest, keep=args.keep)
    print(f"Snapshot -> {target}")
    print(f"  verify: python3 {Path(sys.argv[0]).name} --dest {dest} --verify")
    return 0


def _selftest() -> int:
    import tempfile

    t = Checks("backup_config")
    # Fixed clock base: every snapshot in this self-test names itself from an
    # explicit instant, so no assertion below can hinge on a wall-clock second
    # boundary the test happens to straddle.
    t0 = datetime(2026, 9, 28, 12, 0, 0, tzinfo=timezone.utc)

    def make_tree(td: Path) -> tuple[Path, Path]:
        srv = td / "server"
        cfg = srv / CONFIG_REL
        cfg.parent.mkdir(parents=True, exist_ok=True)
        cfg.write_text(
            json.dumps({"DedicatedOnly": True, "Governor": {"Enabled": True}}),
            encoding="utf-8",
        )
        return srv, cfg

    with tempfile.TemporaryDirectory(prefix="es-backup-test.") as raw:
        td = Path(raw)
        srv, live = make_tree(td)
        dest = td / "offhost"

        try:
            snapshot(srv, td / "server" / "backup")
            t.check("refuses a dest inside the install tree", False)
        except BackupError:
            t.check("refuses a dest inside the install tree", True)

        try:
            snapshot(td / "empty-server", dest)
            t.check("fails loud on a missing live config", False)
        except BackupError:
            t.check("fails loud on a missing live config", True)

        # Second fixed instant, one after t0, so the retention checks below
        # order a stamp across a second boundary without a live clock.
        t1 = t0 + timedelta(seconds=1)

        first, _ = snapshot(srv, dest, now=t0)
        t.check(
            "snapshot holds the live bytes",
            (first / CONFIG_NAME).read_bytes() == live.read_bytes(),
        )
        t.check("a fresh snapshot verifies", verify(dest) == [])

        # An operator edit must survive the next snapshot, and a second copy
        # inside the same second must not overwrite the first.
        live.write_text(json.dumps({"DedicatedOnly": False}), encoding="utf-8")
        second, _ = snapshot(srv, dest, now=t0)
        t.check(
            "a same-second snapshot does not clobber the first",
            first.exists() and second.exists(),
        )
        t.check("both snapshots verify", verify(dest) == [])
        restored_second = json.loads((second / CONFIG_NAME).read_text(encoding="utf-8"))
        t.check("the newer snapshot carries the edit", restored_second["DedicatedOnly"] is False)

        (dest / "empty").mkdir()
        t.check("a stray directory is not counted as a snapshot", len(snapshot_dirs(dest)) == 2)
        t.check("an empty dest reports a failure, not a pass", verify(td / "nothing-here") != [])

        # Retention: keep 2 of 4, with the last stamped a second later.
        third, _ = snapshot(srv, dest, keep=2, now=t0)
        fourth, _ = snapshot(srv, dest, keep=2, now=t1)
        kept = [p.name for p in snapshot_dirs(dest)]
        t.check("prune retains the requested count", len(kept) == 2)
        t.check("prune keeps the newest snapshots", kept[-1] == fourth.name)
        t.check("prune drops the oldest snapshots", first.name not in kept)
        t.check("a same-second copy survives the later stamp", third.name in kept)
        # A same-second counter past nine is where a name sort inverts.
        t.check(
            "a same-second counter past nine keeps its order",
            _snapshot_order("20260928_101500_10") > _snapshot_order("20260928_101500_9")
            and sorted(["20260928_101500_10", "20260928_101500_9"], key=_snapshot_order)
            == ["20260928_101500_9", "20260928_101500_10"],
        )

        # The same inversion through the real suffix loop: a second holding more
        # than ten snapshots is where a text-sorting suffix breaks (`_2` sorts
        # after `_10`), so prune would keep an older snapshot and delete the
        # newest, the one a rerun just took.
        crowd = td / "crowded"
        made = [snapshot(srv, crowd, now=t0)[0] for _ in range(12)]
        t.check(
            "same-second snapshots stay in creation order",
            [p.name for p in snapshot_dirs(crowd)] == [p.name for p in made],
        )
        prune(crowd, 1)
        t.check(
            "prune of a crowded second keeps the newest",
            [p.name for p in snapshot_dirs(crowd)] == [made[-1].name],
        )

        # Corruption is the disaster this catches, so it must be detected.
        victim = snapshot_dirs(dest)[0] / CONFIG_NAME
        victim.write_text("{ truncated", encoding="utf-8")
        t.check("a corrupted snapshot is reported", any("sha256" in p for p in verify(dest)))
        victim.write_bytes(live.read_bytes())
        t.check("a repaired snapshot verifies again", verify(dest) == [])

        out = td / "recovered" / CONFIG_NAME
        restore(dest, snapshot_dirs(dest)[-1].name, out)
        t.check("restore reproduces the snapshot bytes", out.read_bytes() == live.read_bytes())

        try:
            restore(dest, snapshot_dirs(dest)[-1].name, out)
            t.check("restore refuses to clobber an existing file", False)
        except BackupError:
            t.check("restore refuses to clobber an existing file", True)

        try:
            restore(dest, "19990101_000000", out / "x.json")
            t.check("restore rejects an unknown stamp", False)
        except BackupError:
            t.check("restore rejects an unknown stamp", True)

    t.check(
        "a typo'd key is reported as unknown",
        unknown_keys({"NoSuchKnob": 1}) == {"NoSuchKnob"},
    )
    t.check("shipped template keys are known", unknown_keys(read_config(TEMPLATE_JSON)) == set())

    return t.finish()


if __name__ == "__main__":
    raise SystemExit(_run(sys.argv[1:]))
