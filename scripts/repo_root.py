#!/usr/bin/env python3
"""Locate this repository's root from any script under ``scripts/``.

Walking up for marker files instead of counting ``parent.parent`` levels: a
script that moves into a subdirectory then fails loudly here instead of
silently resolving to the parent tree and reading or writing the wrong files.

Run: python3 scripts/repo_root.py --selftest    (wired into `make test`)
"""
from __future__ import annotations

import sys
from pathlib import Path

# Paths that exist together only at this repository's root. Both are required,
# so a stray Makefile in a parent workspace directory cannot be mistaken for it.
MARKERS = ("Makefile", "Source/EfficientServer/EfficientServer.csproj")

USAGE = """\
usage: repo_root.py [--selftest] [-h | --help]

Repository-root lookup used by the scripts in this directory.
  --selftest  run the marker-walk self-test (default with no arguments)
  -h, --help  show this help\
"""


def repo_root(start: Path | None = None) -> Path:
    """Nearest ancestor of ``start`` (default: this file) holding every marker.

    Raises RuntimeError when there is none, rather than returning a plausible
    wrong directory.
    """
    here = (start or Path(__file__)).resolve()
    for candidate in (here, *here.parents):
        if all((candidate / marker).exists() for marker in MARKERS):
            return candidate
    raise RuntimeError(f"no repository root at or above {here}; looked for {', '.join(MARKERS)}")


def _selftest() -> int:
    import tempfile

    failures: list[str] = []

    def check(name: str, cond: bool) -> None:
        if cond:
            print("PASS: " + name)
        else:
            print("FAIL: " + name, file=sys.stderr)
            failures.append(name)

    check("finds the real root from this file", (repo_root() / MARKERS[0]).is_file())

    with tempfile.TemporaryDirectory(prefix="es-repo-root-test.") as td:
        root = Path(td) / "tree"
        for marker in MARKERS:
            (root / marker).parent.mkdir(parents=True, exist_ok=True)
            (root / marker).write_text("", encoding="utf-8")
        nested = root / "scripts" / "deep" / "deeper"
        nested.mkdir(parents=True)
        check("walks up from a nested start", repo_root(nested / "s.py") == root.resolve())
        check("a marker directory is its own root", repo_root(root) == root.resolve())

        # One marker alone must not satisfy the lookup, or a workspace-level
        # Makefile above the repo would shadow the real root. Asserted as "not
        # this directory" rather than "raises": the temp tree may itself sit
        # under a real repository (TMPDIR points into .scratch/), in which case
        # the walk correctly continues upwards instead of failing.
        partial = Path(td) / "partial"
        (partial / "sub").mkdir(parents=True)
        (partial / MARKERS[0]).write_text("", encoding="utf-8")
        found: Path | None = None
        try:
            found = repo_root(partial / "sub" / "s.py")
        except RuntimeError:
            pass  # no root anywhere above: also a correct rejection of `partial`
        check("one marker of two is not a root", found != partial.resolve())

    if failures:
        print("FAIL: repo_root selftest", file=sys.stderr)
        return 1
    print("PASS: repo_root selftest")
    return 0


if __name__ == "__main__":
    argv = sys.argv[1:]
    if argv in (["-h"], ["--help"]):
        print(USAGE)
        raise SystemExit(0)
    if argv not in ([], ["--selftest"]):
        print(f"repo_root.py: unrecognized arguments: {' '.join(argv)}", file=sys.stderr)
        print(USAGE, file=sys.stderr)
        raise SystemExit(2)
    raise SystemExit(_selftest())
