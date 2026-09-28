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
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import TypedDict

from es_cfg_guard import CFG_ENCODING
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


# A snapshot is built under this name and renamed into place, so a run killed
# between the two file writes cannot publish a half snapshot (see snapshot). The
# creating pid is part of the name, so a dead run's staging dir is attributable
# and the next run can reap it; the same pid-attributable temp rule the config
# guard applies beside the installed config (es_cfg_guard._write_atomic).
STAGING_PREFIX = ".staging-"


def _staging_owner(name: str) -> int:
    """The pid that created staging dir ``name``, or 0 when it is not one."""
    head = name[len(STAGING_PREFIX) :].partition(".")[0]
    return int(head) if head.isdigit() else 0


def _sweep_abandoned_staging(dest: Path) -> None:
    """Drop staging dirs left behind by runs that died before publishing.

    A staging dir holds no finished snapshot, and its name matches nothing in
    `snapshot_dirs`, so nothing else in this tool would ever remove it: a host
    whose snapshots are taken by a cron killed by a timeout would accumulate one
    per run forever. A dir whose owning pid is still running belongs to a
    concurrent run and is left alone, exactly like the guard's temp sweep.
    """
    for entry in dest.glob(f"{STAGING_PREFIX}*"):
        owner = _staging_owner(entry.name)
        if not owner:
            continue
        try:
            os.kill(owner, 0)
        except ProcessLookupError:
            shutil.rmtree(entry, ignore_errors=True)
        except OSError:
            pass  # alive (or not ours to signal): not ours to remove


def _new_staging(dest: Path) -> Path:
    _sweep_abandoned_staging(dest)
    return Path(
        tempfile.mkdtemp(prefix=f"{STAGING_PREFIX}{os.getpid()}.", dir=str(dest))
    )


def _publish(staging: Path, target: Path) -> None:
    """Move a finished staging dir onto its final stamp name.

    One rename, so both files appear together or not at all, and a kill before
    it leaves the destination exactly as it was. rename() is also the loud
    option against the stamp collision the existence check above races with: a
    directory that already holds files cannot be replaced, so a concurrent run
    that won the name is reported instead of merged into.
    """
    try:
        os.rename(staging, target)
    except OSError as exc:
        msg = f"could not publish snapshot {target.name}: {exc}"
        raise BackupError(msg) from exc


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

    Rerun safety: a snapshot this call could not finish leaves NOTHING behind.
    It is built in a staging dir and renamed into place, so a kill between the
    two file writes cannot publish a half snapshot (a dir holding one of the two
    files is invisible to `snapshot_dirs`, so it would be neither verified nor
    pruned, and its stamp would stay taken for the retention window), and a
    snapshot that fails its own read-back is removed again before the error is
    raised. Without that, one failed run left a snapshot that made every LATER
    `--verify` and every later `snapshot()` fail, with no rerun able to clear it.
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
    manifest: Manifest = {
        "stamp": target.name,
        "source": str(live),
        "sha256": sha256_of(live),
    }
    # Build under a name no reader matches, then publish with one rename. The
    # stamp in the manifest is the FINAL name, so the manifest is built here,
    # not inside the staging dir, where target.name would read as the staging
    # name. A staging dir left by a killed run is matched by no reader, which is
    # why _new_staging sweeps the ones whose owning pid is gone instead of
    # letting them accumulate under the backup destination.
    dest.mkdir(parents=True, exist_ok=True)
    staging = _new_staging(dest)
    try:
        (staging / CONFIG_NAME).write_bytes(live.read_bytes())
        (staging / MANIFEST_NAME).write_text(
            json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8"
        )
        _publish(staging, target)
    except BaseException:
        # A write or rename that never published leaves the staging dir as the
        # run's only residue; drop it so a retry starts from the state this
        # attempt found.
        shutil.rmtree(staging, ignore_errors=True)
        raise

    problems = verify(dest)
    if problems:
        # The destination is left exactly as this call found it: a snapshot
        # nobody can restore is worse than no snapshot, and leaving it would
        # make every later verify and every later snapshot fail on it.
        shutil.rmtree(target, ignore_errors=True)
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
    """Snapshot dirs, oldest first (see `_snapshot_order`).

    A staging dir (see `snapshot`) is never a snapshot even when it already holds
    both files: it belongs to a run that has not published yet, so counting it
    would let `prune` delete it out from under that run. Only the pid sweep
    removes one.
    """
    if not dest.is_dir():
        return []
    found = [
        p
        for p in dest.iterdir()
        if p.is_dir()
        and not p.name.startswith(STAGING_PREFIX)
        and (p / CONFIG_NAME).is_file()
        and (p / MANIFEST_NAME).is_file()
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
    # The stamp names one directory directly under dest, so resolve it as a
    # name and require the result to still be a child of dest. Joining an
    # unvalidated argument lets `--restore ../../..` read a config-shaped file
    # from anywhere the operator can reach, and the verification below keys on
    # src.name, which a traversing stamp does not match: the copy would be
    # taken from a file no snapshot check ever looked at. A snapshot name is
    # this tool's own stamp plus an optional same-second counter, so anything
    # else is a mistake worth naming rather than resolving.
    if not _SNAPSHOT_NAME.fullmatch(stamp):
        msg = (
            f"'{stamp}' is not a snapshot name; --restore takes a directory name"
            f" under {dest}, as stamped by this tool (YYYYMMDD_HHMMSS, plus _N"
            " for a same-second copy). List them with --verify."
        )
        raise BackupError(msg)
    src = dest / stamp
    if src.parent.resolve() != dest.resolve():
        msg = f"no snapshot '{stamp}' under {dest}"
        raise BackupError(msg)
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

        # The stamp is a directory NAME under dest, not a path. Without the
        # name check a traversing stamp resolved to a config-shaped file
        # anywhere the operator can read, and the per-snapshot verification
        # keyed on src.name never matched it, so the copy was taken from a file
        # no check had looked at. Assert both halves: the traversal is refused,
        # and the planted file outside dest is untouched.
        outside = td / "outside"
        outside.mkdir()
        (outside / CONFIG_NAME).write_text(
            json.dumps({"DedicatedOnly": True, "Source": "planted"}),
            encoding="utf-8",
        )
        planted_bytes = (outside / CONFIG_NAME).read_bytes()
        def refused(stamp: str) -> bool:
            try:
                restore(dest, stamp, out / "stolen.json", force=True)
            except BackupError:
                return True
            return False

        t.check(
            "restore refuses every stamp that is not a directory name",
            all(
                refused(stamp)
                for stamp in (
                    "../outside",
                    f"..{os.sep}outside",
                    str(outside),
                )
            ),
        )
        t.check("the file outside the snapshot root is untouched",
                (outside / CONFIG_NAME).read_bytes() == planted_bytes)
        t.check("a refused traversal writes nothing", not (out / "stolen.json").exists())

    # The property the snapshot protocol actually promises: a run that did not
    # finish leaves the destination as it found it, so the NEXT run is not
    # poisoned by it. A failed run that published its own half-written snapshot
    # made every later verify and every later snapshot fail forever, with no
    # rerun able to clear it.
    with tempfile.TemporaryDirectory(prefix="es-backup-rerun.") as raw:
        td = Path(raw)
        srv, live = make_tree(td)
        dest = td / "offhost"

        # A pre-existing broken snapshot is one the operator has to look at, and
        # it makes the read-back of the NEW snapshot fail with it.
        broken = dest / "20260101_000000"
        broken.mkdir(parents=True)
        (broken / CONFIG_NAME).write_text("{ truncated", encoding="utf-8")
        (broken / MANIFEST_NAME).write_text(
            json.dumps({"stamp": broken.name, "source": "x", "sha256": "0" * 64}) + "\n",
            encoding="utf-8",
        )
        before = {p.name for p in dest.iterdir()}
        try:
            snapshot(srv, dest, now=t0)
            t.check("a snapshot whose read-back fails raises", False)
        except BackupError:
            t.check("a snapshot whose read-back fails raises", True)
        t.check(
            "a failed snapshot is retracted, not published",
            {p.name for p in dest.iterdir()} == before,
        )
        t.check(
            "the failed run left no staging dir behind",
            not [p for p in dest.iterdir() if p.name.startswith(STAGING_PREFIX)],
        )
        # Clear what the operator would clear, and the rerun must succeed.
        shutil.rmtree(broken)
        after, _ = snapshot(srv, dest, now=t0)
        t.check("a rerun after a failure takes a clean snapshot", verify(dest) == [])
        t.check("the rerun's snapshot carries the live bytes",
                (after / CONFIG_NAME).read_bytes() == live.read_bytes())

        # Staging dirs of killed runs are reaped by the next one (dead pid), a
        # live run's is left alone, and neither is ever a snapshot.
        dead_pid = 2**22 - 1  # above the default pid_max: cannot be running
        dead = dest / f"{STAGING_PREFIX}{dead_pid}.abcd"
        dead.mkdir()
        (dead / CONFIG_NAME).write_bytes(live.read_bytes())
        (dead / MANIFEST_NAME).write_text("{}", encoding="utf-8")
        alive = dest / f"{STAGING_PREFIX}{os.getppid()}.efgh"
        alive.mkdir()
        (alive / CONFIG_NAME).write_bytes(live.read_bytes())
        (alive / MANIFEST_NAME).write_text("{}", encoding="utf-8")
        snapshot(srv, dest, now=t1)
        t.check("a staging dir of a dead run is swept", not dead.exists())
        t.check("a staging dir of a live run is kept", alive.is_dir())
        t.check(
            "a staging dir is never counted as a snapshot",
            all(not p.name.startswith(STAGING_PREFIX) for p in snapshot_dirs(dest)),
        )
        t.check("the sweep leaves the snapshots verifying", verify(dest) == [])

    t.check(
        "a typo'd key is reported as unknown",
        unknown_keys({"NoSuchKnob": 1}) == {"NoSuchKnob"},
    )
    t.check("shipped template keys are known", unknown_keys(read_config(TEMPLATE_JSON)) == set())

    return t.finish()


if __name__ == "__main__":
    raise SystemExit(_run(sys.argv[1:]))
