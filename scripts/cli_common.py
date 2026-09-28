#!/usr/bin/env python3
"""Shared CLI contract and selftest bookkeeping for the scripts in this directory.

Every gate here takes the same three shapes of argument: -h/--help prints the
module's USAGE, an unrecognized argument is an error (exit 2), and a module that
ships a `_selftest` also accepts `--selftest` to reach it. Every selftest
collects named PASS/FAIL checks the same way and ends on the same summary.
Both live here once so the flags and the summary text cannot drift between
copies, and so a new gate gets them by calling instead of by copying.

Run via a sibling script; this module is never an entry point itself.
"""
from __future__ import annotations

import sys
from collections.abc import Callable
from typing import NoReturn

__all__ = ["Selftest", "run_cli"]


class Selftest:
    """Named PASS/FAIL check collector shared by the scripts' selftests.

    `check` records a result and keeps going so one run reports every broken
    case; `finish` prints the summary line and yields the process exit code.
    """

    def __init__(self) -> None:
        self.failures: list[str] = []

    def check(self, name: str, cond: bool) -> None:
        if cond:
            print("PASS: " + name)
        else:
            print("FAIL: " + name, file=sys.stderr)
            self.failures.append(name)

    def finish(self, label: str) -> int:
        if self.failures:
            print(f"FAIL: {len(self.failures)} {label} selftest check(s)", file=sys.stderr)
            return 1
        print(f"PASS: {label} selftest")
        return 0


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
    argv = sys.argv[1:]
    if argv in (["-h"], ["--help"]):
        print(usage)
        raise SystemExit(0)
    if selftest is not None and argv == ["--selftest"]:
        raise SystemExit(selftest())
    if argv:
        print(f"{name}: unrecognized arguments: {' '.join(argv)}", file=sys.stderr)
        print(usage, file=sys.stderr)
        raise SystemExit(2)
    raise SystemExit(run())
