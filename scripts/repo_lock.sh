#!/usr/bin/env bash
# Advisory lock for the scripts that destroy and rewrite shared paths.
#
# Sourced, never executed. Callers that wipe a tree another invocation of
# themselves also wipes take the lock first and keep it until they exit:
#
#   source "$SCRIPTDIR/repo_lock.sh"
#   repo_lock build
#   rm -rf "$OUT"
#
# Why: build.sh starts with `rm -rf dist/EfficientServer` and
# `rm -rf Source/EfficientServer/{obj,bin}` before compiling, and install.sh
# does `rm -rf "$DEST"` before copying the built mod over it. Two of them
# running at once (an operator in a second shell, a cron'd `make package`
# landing on top of a `make install`) interleave: one process deletes the
# output directory or the install tree while the other is mid-write or
# mid-copy, and the result is a mod folder holding half an artifact, or a
# build that fails on files another process is removing. Neither run reports
# what happened, because each one's own commands succeeded.
#
# The lock is a separate lock file per name, so the two callers cannot
# deadlock: install.sh holds the "install" lock while build.sh takes the
# "build" lock underneath it, and no path takes them in the other order.
#
# flock(1) is the mechanism: the lock is the open file description, so the
# kernel drops it when the process exits, including a SIGKILL. A
# pid-file-plus-kill -0 scheme would leave a lock behind on every killed run
# and need a staleness rule to recover; this needs none. A host without
# flock(1) is told so rather than left silently unlocked.
#
# Bounded wait, not an unbounded one: a run that waited forever behind a lock
# nobody is holding reads as a hang. The timeout is long enough to outlast a
# normal build and short enough that a stuck holder is reported.

# Seconds a caller waits for the lock before giving up. A cold dotnet
# restore plus compile of the mod project is well under this; several
# concurrent callers (install, package, verify-reproducible) queue behind
# each other, so the wait is per queued run, not for the whole set.
REPO_LOCK_TIMEOUT=900

# Hold an exclusive lock named $1 until this process exits. Prints nothing on
# success; exits 1 with a named error when the lock is unavailable.
repo_lock() {
  local name="$1" dir file
  dir="$ROOT/.scratch/locks"
  mkdir -p "$dir"
  file="$dir/$name.lock"
  if ! command -v flock >/dev/null 2>&1; then
    echo "WARNING: flock(1) not found; running '$name' unlocked." >&2
    echo "WARNING: a concurrent build or install of $ROOT can corrupt it." >&2
    return 0
  fi
  # Fixed descriptor, not a per-call one: repo_lock is called once per script,
  # and a leaked descriptor would keep the lock past the section that wanted
  # it released.
  exec 9>"$file" || {
    echo "ERROR: cannot open lock file $file" >&2
    exit 1
  }
  if ! flock -w "$REPO_LOCK_TIMEOUT" 9; then
    echo "ERROR: another '$name' of $ROOT has held $file for more than" \
      "${REPO_LOCK_TIMEOUT}s; giving up rather than racing it." >&2
    echo "  If no other run is active, the holder was killed and its lock" >&2
    echo "  with it; rerun this command." >&2
    exit 1
  fi
}
