#!/usr/bin/env bash
# Scratch staging-directory lifecycle, shared by the scripts that copy a tree
# onto disk before packaging or re-verifying it (package.sh, verify_reproducible.sh).
#
# Sourced, never executed. The caller exports TMPDIR to a disk-backed directory
# (never the stock tmpfs) and sources this:
#
#   source "$SCRIPTDIR/stage_tmp.sh"
#   STAGE="$(stage_new es-package)"
#   trap 'rm -rf "$STAGE"' EXIT
#
# Why a helper rather than a bare `mktemp -d`: the EXIT trap is the only reaper,
# so a run killed by a tool timeout (these scripts are the slowest in the repo)
# leaves its stage behind, and a bare mktemp name carries no owner, so nothing can
# tell a live run's stage from a dead run's. The stage name here carries the
# creating pid, and stage_new reaps every same-prefix stage whose pid is gone
# before it makes its own. Same rule the Python config guard applies to the
# atomic-write temps it strands beside the installed config
# (es_cfg_guard.ConfigSwap._sweep_abandoned_temps).
#
# A recycled pid makes a dead run's stage look owned, so it survives to the next
# sweep. The failure mode is a directory left on disk, never a live stage removed
# out from under a running script.

# Create $TMPDIR if it does not exist. mktemp honors TMPDIR but not its absence.
stage_tmpdir() {
  mkdir -p "$TMPDIR"
}

# Remove every "$TMPDIR/<prefix>.<pid>.*" whose owning pid is no longer running.
# Prints nothing; safe to call with no match (nullglob off, so the pattern is
# filtered rather than expanded literally).
stage_sweep() {
  local prefix="$1" dir owner
  shopt -s nullglob
  for dir in "$TMPDIR/$prefix".[0-9]*.*; do
    owner="${dir#"$TMPDIR/$prefix".}"; owner="${owner%%.*}"
    kill -0 "$owner" 2>/dev/null || rm -rf "$dir"
  done
  shopt -u nullglob
}

# Reap this prefix's dead stages, then print a fresh one. Callers own the
# returned path and remove it (their EXIT trap, or the caller of this function).
stage_new() {
  local prefix="$1"
  stage_tmpdir
  stage_sweep "$prefix"
  mktemp -d "$TMPDIR/$prefix.$$.XXXXXX"
}
