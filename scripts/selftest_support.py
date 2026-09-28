"""Shared PASS/FAIL collector for the ``--selftest`` entry points.

Every gate script here has a selftest that drives its own pure helpers on
synthetic inputs (the repo-facing gate only fails on tree drift, so it stays
green when the script's own matching logic rots). Those selftests all report
the same way, so the collector and its epilogue live here once: a local
``check`` closure plus the identical "N failing check(s)" tail would otherwise
drift apart across five copies.

One spelling of the result line for every script, so a CI log is parsed the
same way whichever gate produced it.
"""
from __future__ import annotations

import sys

__all__ = ["Checks"]


class Checks:
    """Ordered PASS/FAIL collector; ``finish`` returns the selftest exit code.

    ``label`` names the script, as it appears in the USAGE text.
    """

    def __init__(self, label: str) -> None:
        self._label = label
        self._failures: list[str] = []

    def check(self, name: str, cond: bool) -> None:
        """Record one assertion; a false ``cond`` fails the selftest."""
        if cond:
            print("PASS: " + name)
        else:
            print("FAIL: " + name, file=sys.stderr)
            self._failures.append(name)

    def finish(self) -> int:
        """Print the summary and return 0 (all passed) or 1 (any failed)."""
        if self._failures:
            print(
                f"FAIL: {len(self._failures)} {self._label} selftest check(s)",
                file=sys.stderr,
            )
            return 1
        print(f"PASS: {self._label} selftest")
        return 0
