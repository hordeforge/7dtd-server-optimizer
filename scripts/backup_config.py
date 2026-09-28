#!/usr/bin/env python3
"""Off-host snapshot, verification and restore of the host-only server config.

Two sets of files in the install tree hold operator state nothing regenerates,
because both are edited on the server host and never in this repo:

- `Mods/EfficientServer/Config/efficientserver.json`, the mod's tuning.
- `serverconfig*.xml` in the install root, the game's own settings (ports,
  password, world and generation options). `run_server.sh` copies the
  tuned XML in beside the binary and keeps a one-off `<name>.pre-optimized`, so
  the live set is normally `serverconfig.xml` plus those siblings.

The admin and whitelist file is a third set, and the one that decides who may
in: `AdminFileName` (the shipped default spells it `serveradmin.xml`) under
`UserDataFolder/Saves`, as the settings themselves document. It is found
through those properties, so a host that moved its user data to another disk is
covered where it keeps the file, and a dedicated install with no admin file
under any of those names is a WARNING, not a silently narrower backup.

`install.sh` and `uninstall.sh` preserve the mod config, and `run_server.sh`
keeps the pre-optimized copy, but every one of those copies lives inside the
install tree, so a lost disk or a lost instance takes the backup with the data.
A copy the same disk can lose is not a backup.

This tool moves both sets to a destination the operator names (another disk, a
synced folder, a host off this one) and, in the same pass, proves the copy is
loadable: the JSON is parsed and its keys are checked against the shipped
template, the XML is parsed, and every file's sha256 is recorded in a manifest
that `verify` re-checks later. A backup nobody has read back is a hypothesis.

    python3 scripts/backup_config.py --dest /mnt/backup/es-config
    python3 scripts/backup_config.py --dest /mnt/backup/es-config --verify
    python3 scripts/backup_config.py --dest /mnt/backup/es-config \
        --restore 20260928_101500 --to ./recovered.json
    python3 scripts/backup_config.py --dest /mnt/backup/es-config \
        --restore 20260928_101500 --item serverconfig.xml --to ./recovered.xml
    python3 scripts/backup_config.py --dest /mnt/backup/es-config \
        --restore 20260928_101500 --item serveradmin.xml --to ./recovered-admin.xml

`--verify` is the restore drill's cheap half: it reads every snapshot back the way
a restore would and exits nonzero on the first one that would not load. Run it on
a schedule; a backup whose failure is silent is not a backup. Pass
`--max-age-hours` to that schedule so a job that stopped running is a failure
too, not a directory of last week's healthy snapshots.

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
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import TypedDict

from es_cfg_guard import CFG_ENCODING, owner_pid
from repo_root import repo_root
from selftest_support import Checks

ROOT = repo_root()
TEMPLATE_JSON = ROOT / "config" / "efficientserver.json"

CONFIG_REL = Path("Mods/EfficientServer/Config/efficientserver.json")
MANIFEST_NAME = "manifest.json"
CONFIG_NAME = "efficientserver.json"
# The game's server settings live in serverconfig*.xml directly in the install
# root. A glob rather than a fixed name, because this repo's own launcher
# writes siblings there: run_server.sh copies the tuned XML in beside the
# binary and keeps the operator's original once as `<name>.pre-optimized`,
# which does not end in `.xml` and so needs its own pattern. Both hold operator
# edits and both are unrecoverable, since the tracked
# serverconfig.optimized.xml is a shipped default and the install tree is the
# only place either file has ever existed.
SERVERCONFIG_GLOBS = ("serverconfig*.xml", "serverconfig*.pre-optimized")
# The admin and whitelist file is a third set of host-only state, and the one
# that decides who may in. serverconfig.xml says where the game keeps it:
# `AdminFileName` (the shipped default spells it `serveradmin.xml`) is
# documented in that file as a path relative to `UserDataFolder/Saves`, and
# `UserDataFolder` itself is the game's own override, commented out by default.
# A name resolved through the properties below is the documented location; the
# glob is the same file on a host that keeps it in the install root instead.
ADMIN_FILENAME_PROPERTY = "AdminFileName"
USERDATA_PROPERTY = "UserDataFolder"
DEFAULT_ADMIN_FILENAME = "serveradmin.xml"
DEFAULT_USERDATA_DIR = "UserDataFolder"
SAVES_SUBDIR = "Saves"
ADMIN_GLOB = "serveradmin*.xml"
# The name of the serverconfig whose properties are read. A tree with only
# siblings still resolves, through the first file in the set, because every
# copy of the settings carries the same two properties.
PRIMARY_SERVERCONFIG = "serverconfig.xml"
# Snapshots are small (a JSON file, a few XMLs and a manifest); a fortnight of
# daily copies costs nothing and bounds how far back a restore can reach.
DEFAULT_KEEP = 14
STAMP_FORMAT = "%Y%m%d_%H%M%S"
# A snapshot dir is the stamp, plus "_N" for the Nth copy taken inside that
# second. Both halves are parsed for ordering; see _snapshot_order.
# ASCII digits only, spelled [0-9] rather than \d: Python's \d matches every
# Unicode decimal digit, so a name spelled with fullwidth or Arabic-Indic
# digits parsed as a stamp here, ordered like one (see _snapshot_order), and
# was accepted as a --restore argument. Same rule as
# es_cfg_guard._OWNER_PID_RE.
_SNAPSHOT_NAME = re.compile(r"([0-9]{8}_[0-9]{6})(?:_([0-9]+))?")
# Width of the same-second collision suffix, so directory names sort in
# creation order (see snapshot's suffix loop and snapshot_dirs).
STAMP_SUFFIX_DIGITS = 3
# `--item all` restore: the one selector that writes more than one file, so it
# needs a destination directory rather than a file path.
ALL_ITEMS = "all"

USAGE = """\
Snapshot, verify and restore the host-only server config: the installed
Mods/EfficientServer/Config/efficientserver.json, the serverconfig*.xml in
the install root (the game's ports, password and world settings), and the
admin/whitelist file the settings name through AdminFileName (default
serveradmin.xml, under UserDataFolder/Saves). All of it exists only on the
server host. The install root comes from DS/SEVENDTD_DS_DIR.

  python3 scripts/backup_config.py --dest /mnt/backup/es-config
  python3 scripts/backup_config.py --dest /mnt/backup/es-config --verify
  python3 scripts/backup_config.py --dest /mnt/backup/es-config \\
      --restore 20260928_101500 --to ./recovered.json
  python3 scripts/backup_config.py --dest /mnt/backup/es-config \\
      --restore 20260928_101500 --item serverconfig.xml --to ./recovered.xml
  python3 scripts/backup_config.py --dest /mnt/backup/es-config \\
      --restore 20260928_101500 --item serveradmin.xml --to ./recovered-admin.xml
  python3 scripts/backup_config.py --dest /mnt/backup/es-config \\
      --restore 20260928_101500 --item all --to ./recovered/

--verify is the restore drill's cheap half: it reads every snapshot back the
way a restore would and exits nonzero on the first one that would not load. Add
--max-age-hours N to fail a snapshot set whose newest copy is older than N,
which is what a backup job that stopped running looks like. Run it on a
schedule; a backup whose failure is silent is not a backup.
"""


class BackupError(Exception):
    """An operator-actionable failure; printed without a traceback."""


class Manifest(TypedDict):
    stamp: str
    source: str
    sha256: str
    # serverconfig file name -> sha256, exactly as snapshotted. Empty on a
    # tree with no server config in it, and absent from a manifest written
    # before this tool covered the XML, which is why every read goes through
    # `.get`: a snapshot an operator took last month must still verify, or
    # adding a file to the backup silently invalidated their history.
    serverconfig: dict[str, str]
    # Same shape, for the admin/whitelist files. Absent from a manifest written
    # before this tool covered them, read through `.get` for the same reason.
    admin: dict[str, str]


def utc_stamp(now: datetime | None = None) -> str:
    """Second-resolution UTC name. Suffixing lives in the caller, not here."""
    return (now or datetime.now(timezone.utc)).strftime(STAMP_FORMAT)


def sha256_of_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_config_bytes(path: Path) -> bytes:
    """The raw bytes of a config file, raising BackupError when it cannot be read.

    Split from :func:`parse_config_bytes` so a caller that needs the bytes for
    more than the document (the sha256 in a manifest, the copy written into a
    snapshot dir) reads the file ONCE. ``snapshot`` and ``verify`` each used to
    read the same file two or three times for those three answers.
    """
    try:
        return path.read_bytes()
    except OSError as exc:
        msg = f"unreadable config {path}: {exc}"
        raise BackupError(msg) from exc


def parse_config_bytes(data: bytes, origin: Path) -> dict[str, object]:
    """The document in already-read config bytes, raising BackupError on anything unloadable.

    ``origin`` names the file in the failure message; the bytes may have been
    read by the caller for other reasons. A non-object document (a list, a bare
    number) parses fine and still cannot be a config, so the shape is checked
    here rather than trusted downstream.
    """
    try:
        doc = json.loads(data.decode(CFG_ENCODING))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        msg = f"unreadable config {origin}: {exc}"
        raise BackupError(msg) from exc
    if not isinstance(doc, dict):
        msg = f"config {origin} is a {type(doc).__name__}, not a JSON object"
        raise BackupError(msg)
    return doc


def read_config(path: Path) -> dict[str, object]:
    """Parse a config file, raising BackupError on anything unloadable."""
    return parse_config_bytes(read_config_bytes(path), path)


def read_serverconfig_bytes(path: Path) -> bytes:
    """The raw bytes of a serverconfig XML, raising BackupError when unreadable.

    The XML twin of :func:`read_config_bytes`, and for the same reason: one
    read serves the parse proof, the manifest digest and the snapshot copy.
    """
    try:
        return path.read_bytes()
    except OSError as exc:
        msg = f"unreadable server config {path}: {exc}"
        raise BackupError(msg) from exc


def parse_serverconfig_bytes(data: bytes, origin: Path) -> None:
    """Parse serverconfig XML already in hand, raising BackupError on anything unloadable.

    The XML twin of :func:`parse_config_bytes`, for the same reason: a caller
    that already holds the bytes (for the manifest digest, for the copy written
    into the snapshot) must not reopen the file to prove it parses.
    ``origin`` names the file in the failure message.
    """
    try:
        ET.fromstring(data)
    except ET.ParseError as exc:
        msg = f"unreadable server config {origin}: {exc}"
        raise BackupError(msg) from exc


def read_serverconfig(path: Path) -> None:
    """Parse a serverconfig XML, raising BackupError on anything unloadable.

    The same proof the JSON side gets, for the same reason: a snapshot that
    does not parse is not a backup. ElementTree raises on a truncated or
    malformed document, which is the corruption a text copy actually hits.
    """
    parse_serverconfig_bytes(read_serverconfig_bytes(path), path)


def is_dedicated_install(server_root: Path) -> bool:
    """True when the tree looks like a real dedicated install, not a mod staging dir.

    Decides whether a missing `serverconfig*.xml` is a hard failure or just a
    narrower snapshot. A real install always has one; a tree holding only
    `Mods/EfficientServer/` is a mod being staged for another host, which
    install.sh supports and which has no game config to lose.
    """
    return (server_root / "7DaysToDieServer_Data").is_dir() or (
        server_root / "7DaysToDieServer.x86_64"
    ).exists()


def serverconfig_files(server_root: Path) -> list[Path]:
    """Live server settings in the install root, by name.

    The pre-optimized copy is a sibling the launcher's own glob cannot see, so
    the two patterns are unioned by name rather than left to whichever matches
    first.
    """
    seen: dict[str, Path] = {}
    for pattern in SERVERCONFIG_GLOBS:
        for path in server_root.glob(pattern):
            if path.is_file():
                seen[path.name] = path
    return [seen[name] for name in sorted(seen)]


def serverconfig_property(data: bytes, name: str) -> str | None:
    """The value of one `<property name=... value=.../>` in a serverconfig.

    None when the document declares no such property, which is what a
    commented-out entry looks like once the file is parsed. `data` must
    already have parsed (see parse_serverconfig_bytes); the caller read it to
    prove exactly that.
    """
    for prop in ET.fromstring(data).iter("property"):
        if prop.get("name") == name:
            return prop.get("value")
    return None


def admin_files(server_root: Path, sc_bytes: dict[str, bytes]) -> list[Path]:
    """Live admin/whitelist files in the install tree, by name.

    The documented location comes out of the server settings themselves
    (AdminFileName under UserDataFolder/Saves), so an operator who moved their
    user data to another disk is covered where they actually keep it, and the
    install root is searched as well for a host that keeps the file there
    instead. A candidate that is not a file costs nothing; a real one that is
    never reported is admin accounts and a whitelist nothing here can
    regenerate, so the caller warns when the set comes back empty.
    """
    primary = sc_bytes.get(PRIMARY_SERVERCONFIG) or next(iter(sc_bytes.values()), b"")
    if not primary:
        # No server settings to read a location out of (a mod-only staging
        # tree); the install-root glob below is all that is left to search.
        candidates = list(server_root.glob(ADMIN_GLOB))
        return sorted({p for p in candidates if p.is_file()}, key=lambda p: p.name)
    admin_name = serverconfig_property(primary, ADMIN_FILENAME_PROPERTY) or DEFAULT_ADMIN_FILENAME
    userdata = serverconfig_property(primary, USERDATA_PROPERTY) or DEFAULT_USERDATA_DIR
    candidates = [server_root / userdata / SAVES_SUBDIR / admin_name, *server_root.glob(ADMIN_GLOB)]
    return sorted({p for p in candidates if p.is_file()}, key=lambda p: p.name)


def template_keys(template: Path = TEMPLATE_JSON) -> frozenset[str]:
    """Top-level keys the shipped template declares; a snapshot is reported against it.

    The mod's loader ignores unknown keys silently, so a typo'd key is a knob
    that quietly never applies. check_config_doc.py proves the template itself;
    this only names, in `verify`, what a snapshot carries that the template does
    not. A stray key is a note and not a failure: it neither blocks a restore
    nor makes the snapshot unverifiable.

    One read per call, and the caller decides how many it makes: a loop over
    snapshots hoists this out rather than re-reading the same file per snapshot.
    """
    return frozenset(read_config(template))


def unknown_keys(doc: dict[str, object], known: frozenset[str]) -> set[str]:
    """Top-level keys in ``doc`` that ``known`` (see :func:`template_keys`) does not declare.

    Takes the key set, not the template path, so a caller comparing many
    documents against one template reads it once. Reading it here instead made
    every snapshot in a verify re-read and re-parse the same shipped file.
    """
    return set(doc) - known


def live_config(server_root: Path) -> Path:
    return server_root / CONFIG_REL


# A snapshot is built under this name and renamed into place, so a run killed
# between the two file writes cannot publish a half snapshot (see snapshot). The
# creating pid is part of the name, so a dead run's staging dir is attributable
# and the next run can reap it; the same pid-attributable temp rule the config
# guard applies beside the installed config (es_cfg_guard._write_atomic).
STAGING_PREFIX = ".staging-"


def _staging_owner(name: str) -> int:
    """The pid that created staging dir ``name``, or 0 when it is not one.

    The parse is the guard's (es_cfg_guard.owner_pid), not `str.isdigit()`:
    `isdigit` accepts superscript and non-ASCII digits, so a destination
    holding `.staging-².x` raised ValueError out of the sweep, and an
    unbounded digit run parsed fine and then raised OverflowError from
    os.kill. The destination is the operator's own directory (a synced
    folder, a shared volume), so these names are not ones this tool minted.
    """
    return owner_pid(name[len(STAGING_PREFIX) :].partition(".")[0]) or 0


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
    return Path(tempfile.mkdtemp(prefix=f"{STAGING_PREFIX}{os.getpid()}.", dir=str(dest)))


def _publish(staging: Path, target: Path) -> None:
    """Move a finished staging dir onto its final stamp name.

    One rename, so both files appear together or not at all, and a kill before
    it leaves the destination exactly as it was. rename() is also the loud
    option against the stamp collision the existence check above races with: a
    directory that already holds files cannot be replaced, so a concurrent run
    that won the name is reported instead of merged into.
    """
    try:
        staging.rename(target)
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
    """Copy the live host config into a stamped snapshot dir; return it and problems.

    The snapshot is written and then read back, so a successful return means the
    copy loads, not merely that `cp` exited 0. A live config that is missing or
    unloadable is an error: an empty snapshot dir would look like a healthy backup.
    So is a real dedicated install with no `serverconfig*.xml`, since the run
    would then publish a snapshot whose coverage silently excludes the operator's
    server settings, which is the whole class of loss this tool exists for.

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
    # One read of the live file feeds all three answers below (document,
    # manifest digest, snapshot copy); it used to be read three times.
    live_bytes = read_config_bytes(live)
    doc = parse_config_bytes(live_bytes, live)
    stray = unknown_keys(doc, template_keys())
    if stray:
        print(
            f"WARNING: {live} has keys the shipped template does not:"
            f" {', '.join(sorted(stray))}. They are preserved verbatim; the mod"
            " ignores unknown keys.",
            file=sys.stderr,
        )
    # The game's server settings, read and proven loadable BEFORE the stamp is
    # taken: a truncated serverconfig.xml must fail the run, not get copied and
    # recorded as good.
    serverconfigs = serverconfig_files(server_root)
    if not serverconfigs:
        msg = f"no {SERVERCONFIG_GLOBS[0]} under {server_root}"
        if is_dedicated_install(server_root):
            msg += (
                "; a dedicated install always has one, so this snapshot would"
                " cover the mod config only and leave the operator's server"
                " settings (ports, password, world) unbacked"
            )
            raise BackupError(msg)
        print(
            f"WARNING: {msg}. This looks like a mod-only tree, so there is no"
            " game config to lose; the snapshot covers the mod config only.",
            file=sys.stderr,
        )
    # One read of each live XML feeds all three answers below (parse proof,
    # manifest digest, staged copy); it used to be read three times.
    sc_bytes = {sc.name: read_config_bytes(sc) for sc in serverconfigs}
    for sc in serverconfigs:
        parse_serverconfig_bytes(sc_bytes[sc.name], sc)

    # The admin/whitelist file, resolved through the settings just read and
    # proven loadable the same way: a copy that would not parse is not a
    # backup. Two files wanting one name in the flat snapshot dir is a
    # misconfiguration to name, not to resolve by renaming, since the rename
    # would leave a restore putting the wrong file back.
    admins = admin_files(server_root, sc_bytes)
    clash = {a.name for a in admins} & ({CONFIG_NAME} | {sc.name for sc in serverconfigs})
    if clash:
        msg = (
            f"admin file {', '.join(sorted(clash))} has the same name as another"
            f" file the snapshot copies; rename the game's {ADMIN_FILENAME_PROPERTY}"
            " so each can be restored to its own path"
        )
        raise BackupError(msg)
    if not admins and is_dedicated_install(server_root):
        print(
            f"WARNING: no {ADMIN_GLOB} under {server_root}"
            f" (nor {DEFAULT_USERDATA_DIR}/{SAVES_SUBDIR}/{DEFAULT_ADMIN_FILENAME})."
            " This snapshot does not cover the admin accounts or the whitelist;"
            " if this host keeps them elsewhere, add that path to the backup.",
            file=sys.stderr,
        )
    ad_bytes = {a.name: read_config_bytes(a) for a in admins}
    for admin in admins:
        parse_serverconfig_bytes(ad_bytes[admin.name], admin)

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
        "sha256": sha256_of_bytes(live_bytes),
        "serverconfig": {name: sha256_of_bytes(data) for name, data in sc_bytes.items()},
        "admin": {name: sha256_of_bytes(data) for name, data in ad_bytes.items()},
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
        (staging / CONFIG_NAME).write_bytes(live_bytes)
        for name, data in (*sc_bytes.items(), *ad_bytes.items()):
            (staging / name).write_bytes(data)
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
        raise BackupError(f"snapshot {target.name} does not verify: " + "; ".join(problems))
    prune(dest, keep)
    return target, []


def _snapshot_order(name: str) -> tuple[int, str, int]:
    """Chronological order key: the stamp first, then the same-second counter.

    Sorting on the raw name is wrong once a second holds ten or more copies:
    `..._101500_10` orders before `..._101500_2` bytewise, so prune would treat
    the ninth copy as the oldest and delete the tenth instead.

    A name this tool did not write keeps its own name as its key, but sorts
    AFTER every stamped directory instead of by raw name against the stamps.
    The destination is the operator's own directory, and a directory there
    holding a config and a manifest is a candidate whatever it is called: a
    name spelled with fullwidth or Arabic-Indic digits collates after every
    ASCII stamp, so `prune(keep=2)` read it as the newest copy and rmtree'd two
    REAL snapshots to keep it. Stamped first, undated last, so every consumer
    that wants the newest DATABLE entry gets one.
    """
    m = _SNAPSHOT_NAME.fullmatch(name)
    if m is None:
        return (1, name, 0)
    return (0, m.group(1), int(m.group(2) or 0))


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
    """Delete all but the `keep` newest snapshots; return the ones removed.

    Only a directory named like this tool's stamp is ever deleted. The
    destination belongs to the operator (a synced folder, a shared volume), and
    a snapshot-shaped directory there that carries no stamp is one this tool
    cannot date, cannot restore by name, and cannot prove it wrote: retiring
    the retention window against it costs a real snapshot and buys nothing.
    Undated directories are reported by `--verify` as undated, which is where
    an operator decides what they are.
    """
    stamped = [p for p in snapshot_dirs(dest) if _SNAPSHOT_NAME.fullmatch(p.name)]
    removed = stamped[:-keep] if keep < len(stamped) else []
    for old in removed:
        shutil.rmtree(old)
    return removed


def verify(dest: Path) -> list[str]:
    """Read every snapshot the way a restore would; return one string per failure.

    Checks the bytes against the manifest sha256, then parses the file and notes
    any key the shipped template does not declare; each recorded serverconfig XML
    is checked the same way. An empty or missing dest is reported as a failure:
    "no backup exists" must never read as "backups are healthy".
    """
    dirs = snapshot_dirs(dest)
    if not dirs:
        return [f"no snapshots under {dest}"]
    # The shipped template is the same file for every snapshot, so it is read
    # once here rather than once per snapshot in the loop below. A template this
    # tool cannot read is a failure of the tool, not of any snapshot, and verify
    # answers with problem strings rather than raising, so it is reported as one.
    try:
        known = template_keys()
    except BackupError as exc:
        return [f"cannot bound snapshots against the template: {exc}"]
    problems: list[str] = []
    for d in dirs:
        # A directory this tool cannot name, it cannot restore either: --restore
        # takes the directory name, so an undated one is a snapshot-shaped
        # directory that only an operator can account for. `prune` leaves those
        # alone, so naming them here is where they get seen.
        if _SNAPSHOT_NAME.fullmatch(d.name) is None:
            problems.append(f"{d.name}: not a snapshot name; --restore takes it as typed")
        manifest_path = d / MANIFEST_NAME
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            problems.append(f"{d.name}: unreadable manifest ({exc})")
            continue
        recorded = manifest.get("sha256") if isinstance(manifest, dict) else None
        # One read of the snapshot serves the digest and the parse below; a
        # mismatch short-circuits, so a corrupt snapshot is not parsed at all.
        try:
            config_bytes = read_config_bytes(d / CONFIG_NAME)
        except BackupError as exc:
            problems.append(str(exc))
            continue
        actual = sha256_of_bytes(config_bytes)
        if recorded != actual:
            problems.append(f"{d.name}: sha256 {actual} != manifest {recorded}")
            continue
        try:
            doc = parse_config_bytes(config_bytes, d / CONFIG_NAME)
        except BackupError as exc:
            problems.append(str(exc))
            continue
        stray = unknown_keys(doc, known)
        if stray:
            # A NOTE, not a problem. The mod ignores a key the shipped template
            # does not declare, and the bytes are preserved and hashed either
            # way, so a snapshot carrying one still restores exactly what was
            # live. Reporting it here made the warning snapshot() prints ("they
            # are preserved verbatim") a lie: the same condition failed the
            # read-back, snapshot() deleted what it had just published, and the
            # run exited nonzero. On a host whose config carries one
            # operator-added knob that is permanent, and --verify, documented to
            # run on a schedule, would report the whole set unhealthy forever.
            print(
                f"NOTE: {d.name}: keys not in the shipped template: {sorted(stray)}"
                " (preserved verbatim; the mod ignores them)",
                file=sys.stderr,
            )
        problems.extend(_verify_serverconfigs(d, manifest))
        problems.extend(_verify_admins(d, manifest))
    return problems


def _verify_recorded(d: Path, manifest: object, key: str, label: str) -> list[str]:
    """Check every file of one manifest key the way a restore would.

    A name in the manifest with no file beside it is the failure a bare sha256
    comparison cannot see: the record exists, the bytes it stands for are gone,
    and a restore of that snapshot would fail at the copy instead of here.
    """
    if not isinstance(manifest, dict):
        return []
    recorded = manifest.get(key)
    if not isinstance(recorded, dict):
        # A manifest written before this tool covered the XML, or the admin
        # files. Its snapshot is what the operator actually has, so it still
        # verifies.
        return []
    problems: list[str] = []
    for name in sorted(recorded):
        path = d / name
        if not path.is_file():
            problems.append(f"{d.name}: {label} {name} is in the manifest but missing")
            continue
        expected = recorded[name]
        # One read serves the digest and the parse proof below; the array form
        # read the same file twice, once per answer.
        try:
            data = read_serverconfig_bytes(path)
        except BackupError as exc:
            problems.append(str(exc))
            continue
        actual = sha256_of_bytes(data)
        if actual != expected:
            problems.append(f"{d.name}: {name} sha256 {actual} != manifest {expected}")
            continue
        try:
            parse_serverconfig_bytes(data, path)
        except BackupError as exc:
            problems.append(str(exc))
    return problems


def _verify_serverconfigs(d: Path, manifest: object) -> list[str]:
    return _verify_recorded(d, manifest, "serverconfig", "serverconfig")


def _verify_admins(d: Path, manifest: object) -> list[str]:
    return _verify_recorded(d, manifest, "admin", "admin file")


def newest_age_hours(dest: Path, now: datetime | None = None) -> float | None:
    """Age of the newest DATABLE snapshot in hours, or None when none is.

    A backup job that stopped running leaves a directory full of snapshots that
    all verify, which is exactly the failure `--verify` on its own cannot see.

    The newest entry of `snapshot_dirs` is not that snapshot when the
    destination also holds a directory this tool did not stamp, or one stamped
    with a date that does not exist (a hand-copied `99999999_999999`): walk
    back to the newest entry that carries a date. None is returned only when
    nothing under the destination does, which `_staleness` reports as a failure
    rather than passing quietly.
    """
    now = now or datetime.now(timezone.utc)
    for d in reversed(snapshot_dirs(dest)):
        m = _SNAPSHOT_NAME.fullmatch(d.name)
        if m is None:
            continue
        try:
            taken = datetime.strptime(m.group(1), STAMP_FORMAT).replace(tzinfo=timezone.utc)
        except ValueError:
            continue
        return (now - taken).total_seconds() / 3600.0
    return None


def restore(
    dest: Path,
    stamp: str,
    to: Path,
    *,
    item: str = CONFIG_NAME,
    force: bool = False,
) -> Path:
    """Copy verified snapshot file(s) to `to`. Refuses to clobber them silently.

    Restoring over the live config is allowed, but only when the caller says so
    explicitly: the live file is the state being recovered, and an unexpected
    overwrite of it is the one mistake a restore tool can make unrecoverable.

    `item` names one file in the snapshot, or `all` to write the whole set into
    `to` as a directory. It defaults to the mod config, so the original
    invocation restores exactly what it always did.
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
    if item == ALL_ITEMS:
        return _restore_all(src, to, force=force)
    # --item names one file beside the config inside the snapshot, so it takes
    # the same bare-name rule as the stamp above. Joined unchecked,
    # `--item ../elsewhere/x.json` passed `(src / item).is_file()` and was
    # copied: verification keys on the snapshot's own files, and a traversing
    # item names one no check ever looked at, yet the printed `cp` handed the
    # operator an install command for it.
    if item != Path(item).name or item in ("", ".", ".."):
        msg = (
            f"'{item}' is not a file name inside a snapshot; --item takes a bare"
            f" name under {src} (one of: {', '.join(_snapshot_files(src)) or 'none'})"
        )
        raise BackupError(msg)
    if not (src / item).is_file():
        available = ", ".join(_snapshot_files(src)) or "none"
        msg = f"snapshot '{stamp}' has no file '{item}' (have: {available})"
        raise BackupError(msg)
    if to.exists() and not force:
        msg = f"{to} exists; pass --force to overwrite it"
        raise BackupError(msg)
    to.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src / item, to)
    return to


def _snapshot_files(src: Path) -> list[str]:
    """Every file a snapshot carries, manifest excluded, for error messages."""
    return sorted(p.name for p in src.iterdir() if p.is_file() and p.name != MANIFEST_NAME)


def _live_hint(name: str) -> str:
    """Where a restored file goes, so the printed `cp` is the right one.

    The mod config lives under `Mods/EfficientServer/Config/`; the game's own
    settings sit in the install root next to the binary, which is where
    run_server.sh put them. The admin file is neither: the game reads it from
    `UserDataFolder/Saves`, or wherever `AdminFileName` in the settings points.
    """
    if name == CONFIG_NAME:
        return f"<DS>/Mods/EfficientServer/Config/{CONFIG_NAME}"
    if Path(name).match(ADMIN_GLOB):
        return f"<UserDataFolder>/{SAVES_SUBDIR}/{name}"
    return f"<DS>/{name}"


def _restore_all(src: Path, to: Path, *, force: bool) -> Path:
    """Write every file of one snapshot into the directory `to`.

    All or nothing: a half-restored set is the state an operator cannot tell
    from a good one, so every existing target is checked before any is written
    and the copy is a directory move that cannot interleave a failure across
    files the way a per-file loop could.
    """
    if to.exists() and not to.is_dir():
        msg = f"{to} exists and is not a directory; --item all needs one"
        raise BackupError(msg)
    names = _snapshot_files(src)
    if not names:
        msg = f"snapshot '{src.name}' holds no files to restore"
        raise BackupError(msg)
    for name in names:
        if (to / name).exists() and not force:
            msg = f"{to / name} exists; pass --force to overwrite it"
            raise BackupError(msg)
    to.mkdir(parents=True, exist_ok=True)
    for name in names:
        shutil.copy2(src / name, to / name)
    return to


def _parse_args(argv: list[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(
        prog="backup_config.py",
        # Spelled out instead of derived: --selftest is the one invocation that
        # needs no --dest, and the derived line would show it as optional for
        # every other one.
        usage="backup_config.py [-h] --dest DIR [--verify [--max-age-hours N]]\n"
        "                 [--restore STAMP [--item NAME] --to PATH] [--force]\n"
        "                 [--keep N] [--selftest]",
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
        "--max-age-hours",
        type=float,
        default=None,
        metavar="N",
        help="with --verify, also fail when the newest snapshot is older than N "
        "hours; off by default, since the right cadence is the operator's",
    )
    p.add_argument(
        "--restore",
        default=None,
        metavar="STAMP",
        help="restore one snapshot by its stamp; needs --to PATH",
    )
    p.add_argument(
        "--item",
        default=CONFIG_NAME,
        metavar="NAME",
        help=f"file to restore: {CONFIG_NAME} (default), a serverconfig*.xml "
        f"or admin file from the snapshot, or '{ALL_ITEMS}' to write the whole "
        "set into a directory",
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
    if args.max_age_hours is not None and not args.verify:
        p.error("--max-age-hours only applies to --verify")
    if args.max_age_hours is not None and args.max_age_hours <= 0:
        p.error(f"--max-age-hours must be positive, got {args.max_age_hours}")
    if args.restore and args.item == ALL_ITEMS and args.to is not None and args.to.suffix:
        p.error(
            f"--item {ALL_ITEMS} writes every file into a directory, so --to must be"
            f" a directory path without a file extension (got '{args.to}')"
        )
    # prune() deletes every snapshot outside the retained window, so a --keep
    # below 1 turns a typo into a wiped backup history.
    if args.keep < 1:
        p.error(f"--keep must be at least 1, got {args.keep}")
    return args


def default_server_root() -> Path:
    """The dedicated install root to snapshot, or a BackupError naming why not.

    An EXPORTED-BUT-EMPTY value is refused, not treated as unset: the same guard
    install.sh, uninstall.sh, run_server.sh and harness_common carry, because
    this tool copies an install the operator named and a mistyped
    ``SEVENDTD_DS_DIR=`` that fell through to another candidate would back up the
    wrong tree.
    """
    for var in ("SEVENDTD_DS_DIR", "DS"):
        if var in os.environ and not os.environ[var].strip():
            msg = f"{var} is set but empty; pass a real install dir or unset it"
            raise BackupError(msg)
    raw = os.environ.get("SEVENDTD_DS_DIR") or os.environ.get("DS") or ""
    # The emptiness test is on the raw value, never on the Path: Path("") renders
    # as ".", so a truthiness test on the rendered root could never see it and
    # the caller's "no server install root" guard was unreachable, leaving an
    # unnamed run to snapshot whatever the working directory happens to be.
    if not raw.strip():
        msg = "no server install root: set SEVENDTD_DS_DIR (or DS) to the dedicated install"
        raise BackupError(msg)
    return Path(raw).expanduser()


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
        if args.max_age_hours is not None:
            problems.extend(_staleness(dest, args.max_age_hours))
        for problem in problems:
            print(f"FAIL: {problem}", file=sys.stderr)
        if problems:
            return 1
        print(f"OK: {len(snapshot_dirs(dest))} snapshot(s) verify under {dest}")
        return 0
    if args.restore:
        out = restore(dest, args.restore, args.to, item=args.item, force=args.force)
        print(f"Restored {args.restore} -> {out}")
        if args.item == ALL_ITEMS:
            for name in _snapshot_files(out):
                print(f"  cp -a '{out / name}' '{_live_hint(name)}'")
        else:
            print(f"  cp -a '{out}' '{_live_hint(args.item)}'")
        # The reload is for the mod config only. The game reads serverconfig.xml
        # once at boot, so telling an operator to `es reload` after putting one
        # back is advice that changes nothing, and telling them to only reload
        # after restoring the whole set leaves the server on the old settings.
        if args.item in (CONFIG_NAME, ALL_ITEMS):
            print("  es reload            # or restart the server")
        if args.item != CONFIG_NAME:
            print("  restart the server   # the game reads serverconfig.xml at boot")
        return 0
    server_root = default_server_root()
    target, _ = snapshot(server_root, dest, keep=args.keep)
    print(f"Snapshot -> {target}")
    covered = _snapshot_files(target)
    print(f"  covered: {', '.join(covered) if covered else 'nothing'}")
    print(f"  verify: python3 {Path(sys.argv[0]).name} --dest {dest} --verify")
    return 0


def _staleness(dest: Path, max_age_hours: float, *, now: datetime | None = None) -> list[str]:
    """The freshness complaint `--max-age-hours` exists to make.

    Every snapshot in a dead backup set still verifies, so a job that stopped
    running a month ago reads exactly like a healthy one. Nothing else in this
    tool can see that, which is why the check is available at all.

    `now` is injectable so the selftest can assert both sides of the limit
    against a fixed clock instead of whatever day it happens to run.
    """
    age = newest_age_hours(dest, now)
    if age is None:
        return [f"no snapshot under {dest} carries a readable stamp to age"]
    if age > max_age_hours:
        return [
            (
                f"newest snapshot under {dest} is {age:.1f}h old, past the"
                f" {max_age_hours:g}h limit; the backup job is not running"
            )
        ]
    return []


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

        # A directory in the destination that this tool did not stamp. The
        # destination is the operator's own (a synced folder, a shared volume),
        # and a name spelled with fullwidth digits (`\d` in Python matches
        # every Unicode decimal digit, and these collate after every ASCII
        # stamp) is one prune used to read as the newest snapshot, keeping it
        # and deleting a real one, while --max-age-hours lost the real newest
        # and reported the whole set as undated.
        fullwidth = str.maketrans("0123456789", "".join(chr(0xFF10 + d) for d in range(10)))
        odd = td / "odd"
        older, _ = snapshot(srv, odd, now=t0)
        newer, _ = snapshot(srv, odd, now=t1)
        alien = odd / t0.strftime(STAMP_FORMAT).translate(fullwidth)
        shutil.copytree(older, alien)
        t.check(
            "an unstamped directory sorts after every real snapshot",
            snapshot_dirs(odd)[-1].name == alien.name,
        )
        t.check(
            "prune retires the oldest real snapshot, never the unstamped one",
            prune(odd, 1) == [older]
            and {p.name for p in odd.iterdir()} == {newer.name, alien.name},
        )
        t.check(
            "an unstamped directory is reported rather than ignored",
            any(alien.name in p for p in verify(odd)),
        )
        try:
            restore(odd, alien.name, td / "alien-out.json")
            undated_refused = False
        except BackupError:
            undated_refused = True
        t.check("--restore refuses an unstamped directory name", undated_refused)
        t.check(
            "the age gate reads the newest real snapshot beside an unstamped one",
            newest_age_hours(odd, now=t1 + timedelta(hours=30)) == 30.0,
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
        t.check(
            "the file outside the snapshot root is untouched",
            (outside / CONFIG_NAME).read_bytes() == planted_bytes,
        )
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
        t.check(
            "the rerun's snapshot carries the live bytes",
            (after / CONFIG_NAME).read_bytes() == live.read_bytes(),
        )

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

        # A staging dir whose pid half is not a pid this host could have
        # assigned is not a temp of this protocol, and the sweep must LEAVE it
        # rather than raise: the destination is the operator's own directory, so
        # the names there are not ones this tool minted. `.staging-².x` raised
        # ValueError from int() and a 20-digit run raised OverflowError from
        # os.kill, both out of snapshot().
        hostile = [
            dest / f"{STAGING_PREFIX}\N{SUPERSCRIPT TWO}.ijkl",
            dest / f"{STAGING_PREFIX}{'9' * 20}.mnop",
        ]
        for stray in hostile:
            stray.mkdir()
        snapshot(srv, dest, now=t1 + timedelta(seconds=1))
        t.check(
            "a staging dir with a non-ASCII pid is left in place, not raised on",
            all(s.is_dir() for s in hostile),
        )
        for stray in hostile:
            shutil.rmtree(stray)

    shipped = template_keys()
    t.check(
        "a typo'd key is reported as unknown",
        unknown_keys({"NoSuchKnob": 1}, shipped) == {"NoSuchKnob"},
    )
    t.check(
        "shipped template keys are known",
        unknown_keys(read_config(TEMPLATE_JSON), shipped) == set(),
    )
    # The read and parse halves must agree with the combined read_config, or a
    # caller reusing one read of the bytes would see a different verdict than
    # the old two-read path did.
    t.check(
        "parse_config_bytes agrees with read_config on a good file",
        parse_config_bytes(TEMPLATE_JSON.read_bytes(), TEMPLATE_JSON) == read_config(TEMPLATE_JSON),
    )
    nonobject_refused = False
    try:
        parse_config_bytes(b"[]", TEMPLATE_JSON)
    except BackupError:
        nonobject_refused = True
    t.check(
        "parse_config_bytes rejects a non-object document like read_config did",
        nonobject_refused,
    )

    # The game's server settings, which the install tree holds and nothing
    # regenerates. Every check above ran against a mod-only tree, where a
    # snapshot legitimately covers one file; these run against a real dedicated
    # install, where the ports, password and world settings are the other half
    # of what a lost disk takes.
    with tempfile.TemporaryDirectory(prefix="es-backup-serverconfig.") as raw:
        td = Path(raw)
        srv, live = make_tree(td)
        (srv / "7DaysToDieServer_Data").mkdir()
        sc = srv / "serverconfig.xml"
        sc.write_text(
            '<?xml version="1.0" encoding="UTF-8"?>\n'
            "<ServerSettings><ServerName>Holdout</ServerName>"
            "<ServerPassword>hunter2</ServerPassword></ServerSettings>\n",
            encoding="utf-8",
        )
        # The sibling run_server.sh keeps of the operator's original.
        pre = srv / "serverconfig.optimized.xml.pre-optimized"
        pre.write_text(
            "<ServerSettings><ServerName>Before</ServerName></ServerSettings>\n", encoding="utf-8"
        )
        dest = td / "offhost"
        # Not inside the install tree, like every other dest here.
        target, _ = snapshot(srv, dest, now=t0)

        t.check(
            "a snapshot carries every live serverconfig, not just the first",
            (target / "serverconfig.xml").is_file()
            and (target / "serverconfig.optimized.xml.pre-optimized").is_file(),
        )
        t.check(
            "the snapshot reproduces the live serverconfig bytes",
            (target / "serverconfig.xml").read_bytes() == sc.read_bytes(),
        )
        t.check("a snapshot with serverconfigs verifies", verify(dest) == [])

        # Corruption in the XML is the disaster this catches, and a sha256
        # check alone is what catches it.
        (target / "serverconfig.xml").write_text("<ServerSettings><Server", encoding="utf-8")
        t.check(
            "a corrupted serverconfig is reported",
            any("serverconfig.xml" in p for p in verify(dest)),
        )
        (target / "serverconfig.xml").write_bytes(sc.read_bytes())
        t.check("a repaired serverconfig verifies again", verify(dest) == [])

        # A record with no file beside it: the manifest promises a file the
        # snapshot no longer has, which a plain hash comparison cannot see.
        (target / "serverconfig.optimized.xml.pre-optimized").unlink()
        t.check(
            "a serverconfig in the manifest but missing from the snapshot is reported",
            any("missing" in p for p in verify(dest)),
        )
        try:
            restore(dest, target.name, td / "x.json", item=ALL_ITEMS)
            t.check("restore refuses a snapshot whose serverconfig is gone", False)
        except BackupError:
            t.check("restore refuses a snapshot whose serverconfig is gone", True)
        # A manifest written before this tool covered the XML must still
        # verify, or adding a file to the backup silently retired an
        # operator's existing snapshot history.
        legacy = target / MANIFEST_NAME
        legacy.write_text(
            json.dumps(
                {
                    "stamp": target.name,
                    "source": str(live),
                    "sha256": sha256_of_bytes((target / CONFIG_NAME).read_bytes()),
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )
        t.check("a manifest without a serverconfig key still verifies", verify(dest) == [])

        # A truncated live XML must fail the run, not be copied and recorded.
        snapshot(srv, dest, now=t1)
        sc.write_text("<ServerSettings", encoding="utf-8")
        before = {p.name for p in dest.iterdir()}
        try:
            snapshot(srv, dest, now=t1)
            t.check("an unreadable live serverconfig raises", False)
        except BackupError:
            t.check("an unreadable live serverconfig raises", True)
        t.check(
            "the failed run published nothing",
            {p.name for p in dest.iterdir()} == before,
        )
        t.check(
            "the failed run left no staging dir behind",
            not [p for p in dest.iterdir() if p.name.startswith(STAGING_PREFIX)],
        )
        sc.write_text(
            '<?xml version="1.0" encoding="UTF-8"?>\n'
            "<ServerSettings><ServerName>Holdout</ServerName></ServerSettings>\n",
            encoding="utf-8",
        )

        # A real install with no serverconfig at all would silently narrow the
        # backup, so the run fails instead. A mod-only staging tree is a
        # supported install.sh target and only warns.
        fresh = td / "fresh"
        cfg = fresh / CONFIG_REL
        cfg.parent.mkdir(parents=True, exist_ok=True)
        cfg.write_text(json.dumps({"DedicatedOnly": True}), encoding="utf-8")
        (fresh / "7DaysToDieServer_Data").mkdir()
        try:
            snapshot(fresh, td / "no-sc", now=t0)
            t.check("a dedicated install with no serverconfig raises", False)
        except BackupError:
            t.check("a dedicated install with no serverconfig raises", True)
        only_mod = td / "modonly"
        cfg2 = only_mod / CONFIG_REL
        cfg2.parent.mkdir(parents=True, exist_ok=True)
        cfg2.write_text(json.dumps({"DedicatedOnly": True}), encoding="utf-8")
        staged, _ = snapshot(only_mod, td / "mod-sc", now=t0)
        t.check(
            "a mod-only staging tree snapshots with a warning, not a failure",
            _snapshot_files(staged) == [CONFIG_NAME],
        )

        # Restore the whole set, and restore one member of it.
        latest = snapshot_dirs(td / "offhost")[-1]
        out = td / "recovered" / "set"
        restore(td / "offhost", latest.name, out, item=ALL_ITEMS)
        t.check(
            "an --item all restore writes the mod config and the serverconfigs",
            (out / CONFIG_NAME).read_bytes() == (latest / CONFIG_NAME).read_bytes()
            and (out / "serverconfig.xml").read_bytes()
            == (latest / "serverconfig.xml").read_bytes(),
        )
        try:
            restore(td / "offhost", latest.name, out, item=ALL_ITEMS)
            t.check("--item all refuses to clobber an existing file", False)
        except BackupError:
            t.check("--item all refuses to clobber an existing file", True)
        restore(td / "offhost", latest.name, out, item=ALL_ITEMS, force=True)
        t.check("--item all --force overwrites", (out / CONFIG_NAME).is_file())

        one = td / "recovered" / "serverconfig.xml"
        restore(td / "offhost", latest.name, one, item="serverconfig.xml")
        t.check(
            "a single serverconfig restores byte-exact",
            one.read_bytes() == (latest / "serverconfig.xml").read_bytes(),
        )
        try:
            restore(td / "offhost", latest.name, one, item="serverconfig.optimized.xml")
            t.check("restoring a file the snapshot lacks raises", False)
        except BackupError:
            t.check("restoring a file the snapshot lacks raises", True)

    # The admin/whitelist file: the third set of host-only state, the one that
    # decides who may in, and the one an install-root glob never sees because
    # the game keeps it under UserDataFolder/Saves.
    with tempfile.TemporaryDirectory(prefix="es-backup-admin.") as raw:
        td = Path(raw)
        srv, _ = make_tree(td)
        (srv / "7DaysToDieServer_Data").mkdir()
        sc = srv / PRIMARY_SERVERCONFIG
        sc.write_text(
            '<?xml version="1.0" encoding="UTF-8"?>\n'
            '<ServerSettings><property name="AdminFileName" value="serveradmin.xml"/>'
            '<property name="UserDataFolder" value="UserDataFolder"/>'
            "</ServerSettings>\n",
            encoding="utf-8",
        )
        admin = srv / DEFAULT_USERDATA_DIR / SAVES_SUBDIR / DEFAULT_ADMIN_FILENAME
        admin.parent.mkdir(parents=True)
        admin.write_text(
            '<ServerSettings><Whitelist><player id="76561190000000000"/></Whitelist>'
            "</ServerSettings>\n",
            encoding="utf-8",
        )
        dest = td / "offhost"
        target, _ = snapshot(srv, dest, now=t0)

        t.check(
            "the admin file is covered where the settings say it lives",
            (target / DEFAULT_ADMIN_FILENAME).read_bytes() == admin.read_bytes(),
        )
        t.check("a snapshot with the admin file verifies", verify(dest) == [])
        t.check(
            "the live hint points the admin file back at UserDataFolder",
            _live_hint(DEFAULT_ADMIN_FILENAME) == "<UserDataFolder>/Saves/serveradmin.xml",
        )

        # The same file on a host that moved its user data elsewhere, named by
        # the game's own property. A fixed path would miss it, and the state it
        # holds is the one nobody can re-derive.
        moved = td / "moved"
        moved_srv, _ = make_tree(moved)
        (moved_srv / "7DaysToDieServer_Data").mkdir()
        far = td / "fast-volume"
        (moved_srv / PRIMARY_SERVERCONFIG).write_text(
            '<?xml version="1.0" encoding="UTF-8"?>\n'
            f'<ServerSettings><property name="AdminFileName" value="serveradmin.xml"/>'
            f'<property name="UserDataFolder" value="{far}"/>'
            "</ServerSettings>\n",
            encoding="utf-8",
        )
        moved_admin = far / SAVES_SUBDIR / DEFAULT_ADMIN_FILENAME
        moved_admin.parent.mkdir(parents=True)
        moved_admin.write_text(admin.read_text(encoding="utf-8"), encoding="utf-8")
        moved_snap, _ = snapshot(moved_srv, td / "moved-offhost", now=t0)
        t.check(
            "a relocated UserDataFolder is covered through the settings",
            (moved_snap / DEFAULT_ADMIN_FILENAME).read_bytes() == moved_admin.read_bytes(),
        )

        # Corruption and a vanished file are the two failures the manifest's
        # record of the admin file exists to catch, exactly as for the XML.
        (target / DEFAULT_ADMIN_FILENAME).write_text("<ServerSettings><Whitelist", encoding="utf-8")
        t.check(
            "a corrupted admin file is reported",
            any(DEFAULT_ADMIN_FILENAME in p for p in verify(dest)),
        )
        (target / DEFAULT_ADMIN_FILENAME).write_bytes(admin.read_bytes())
        t.check("a repaired admin file verifies again", verify(dest) == [])
        (target / DEFAULT_ADMIN_FILENAME).unlink()
        t.check(
            "an admin file in the manifest but missing from the snapshot is reported",
            any("missing" in p for p in verify(dest)),
        )
        shutil.copy2(admin, target / DEFAULT_ADMIN_FILENAME)

        out = td / "recovered-admin.xml"
        restore(dest, target.name, out, item=DEFAULT_ADMIN_FILENAME)
        t.check(
            "the admin file restores byte-exact",
            out.read_bytes() == admin.read_bytes(),
        )

        # No admin file anywhere is a WARNING, not a failed run: the game
        # creates one on first use and a mod-only staging tree has none, while
        # failing here would train operators to ignore a loud tool.
        bare = td / "bare"
        cfg = bare / CONFIG_REL
        cfg.parent.mkdir(parents=True)
        cfg.write_text(json.dumps({"DedicatedOnly": True}), encoding="utf-8")
        (bare / "7DaysToDieServer_Data").mkdir()
        (bare / PRIMARY_SERVERCONFIG).write_text(
            '<?xml version="1.0" encoding="UTF-8"?>\n<ServerSettings/>\n', encoding="utf-8"
        )
        bare_snap, _ = snapshot(bare, td / "bare-offhost", now=t0)
        t.check(
            "a dedicated install with no admin file still snapshots",
            _snapshot_files(bare_snap) == [CONFIG_NAME, PRIMARY_SERVERCONFIG],
        )

    # A backup job that stopped running leaves snapshots that all verify, which
    # is the one failure the read-back cannot see. --max-age-hours is the only
    # thing here that notices.
    with tempfile.TemporaryDirectory(prefix="es-backup-age.") as raw:
        td = Path(raw)
        srv, _ = make_tree(td)
        dest = td / "offhost"
        old = datetime(2026, 9, 1, 12, 0, 0, tzinfo=timezone.utc)
        snapshot(srv, dest, now=old)
        t.check("a week-old snapshot set still verifies", verify(dest) == [])
        t.check(
            "the newest snapshot's age is reported",
            newest_age_hours(dest, now=old + timedelta(hours=30)) == 30.0,
        )
        t.check(
            "a stale snapshot set fails the age limit",
            _staleness(dest, 24.0, now=old + timedelta(hours=30)) != [],
        )
        t.check(
            "a fresh snapshot set passes the age limit",
            _staleness(dest, 48.0, now=old + timedelta(hours=30)) == [],
        )
        t.check("an empty dest has no age to report", newest_age_hours(td / "nothing") is None)
        t.check(
            "an empty dest fails the age limit",
            _staleness(td / "nothing", 24.0, now=old + timedelta(hours=30)) != [],
        )

    return t.finish()


if __name__ == "__main__":
    raise SystemExit(_run(sys.argv[1:]))
