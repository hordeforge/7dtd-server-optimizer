#!/usr/bin/env python3
"""Shared CLI argument contract for the scripts in this directory.

Every gate here takes the same three shapes of argument: -h/--help prints the
module's USAGE, an unrecognized argument is an error (exit 2), and a module that
ships a `_selftest` also accepts `--selftest` to reach it. Dispatch lives here
once so the flags cannot drift between copies, and so a new gate gets them by
calling instead of by copying.

The PASS/FAIL collector those selftests record into is `Checks` in
`selftest_support`; this module owns argv, that one owns results.

Run via a sibling script; this module is never an entry point itself.
"""
from __future__ import annotations

import sys
from collections.abc import Callable
from typing import NoReturn

__all__ = ["preflight_usage", "run_cli"]


def preflight_usage(name: str, usage: str, selftest: bool = False) -> None:
    """Answer `-h`/`--help` and reject unknown arguments, then return.

    For a script whose module-level imports can fail before its own dispatch
    runs: the live-server harnesses import the 7dtd-loadgen sibling at import
    time, so with that tree absent even `--help` died on a traceback. Call
    this right after USAGE is defined and before those imports; the full
    dispatch still goes through run_cli, which only ever sees an empty argv
    here. Pass `selftest=True` for a module that ships one, so `--selftest`
    survives this pass instead of reading as an unknown argument.
    """
    argv = sys.argv[1:]
    if argv in (["-h"], ["--help"]):
        print(usage)
        raise SystemExit(0)
    if selftest and argv == ["--selftest"]:
        return
    if argv:
        print(f"{name}: unrecognized arguments: {' '.join(argv)}", file=sys.stderr)
        print(usage, file=sys.stderr)
        raise SystemExit(2)


def run_cli(
    name: str,
    usage: str,
    run: Callable[[], int],
    selftest: Callable[[], int] | None = None,
) -> NoReturn:
    """Dispatch argv for a script whose real work is `run() -> exit code`.

    `selftest`, when given, is the target of `--selftest`; a module whose bare
    invocation already runs its selftest passes it as `run` too, which is how
    both spellings land on the same work.
    """
    preflight_usage(name, usage, selftest=selftest is not None)
    if selftest is not None and sys.argv[1:] == ["--selftest"]:
        raise SystemExit(selftest())
    raise SystemExit(run())
