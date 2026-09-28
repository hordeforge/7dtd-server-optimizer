# Contributing to 7dtd-server-optimizer

The whole CI gate is one local command. If `make test` passes on your machine,
CI will pass: it runs exactly `make test` on every PR and on pushes to main
(`.github/workflows/ci.yml`). A second job runs the game-type-free harness
(`Source/EfficientServer.Tests`) on Windows, because the shipped DLL is
OS-neutral managed code that a Windows host loads; the shell, Makefile and
Python gates stay Linux, which is what the build tooling targets.

## Requirements

- Linux host (scripts assume Steam library paths, GNU coreutils, `taskset`)
- .NET SDK, 8.0.4xx band, pinned by [`global.json`](global.json). The Makefile
  picks up a local install from `~/.cache/dotnet-sdk` or `~/.dotnet`
  automatically; otherwise put `dotnet` on `PATH`
- `shellcheck`, `ruff`, `mypy`, and Python 3 (`make test`)

`ruff` and `mypy` are pinned to the exact versions `make test` and CI require
(`make ruff-version`, `make mypy-version`; today `ruff==0.16.4` and
`mypy==2.1.0`), because rule and checker behavior differs between releases and a
mismatched local run is a false signal either way. Install them in one step:

```bash
uv tool install ruff=="$(make -s ruff-version)" mypy=="$(make -s mypy-version)"
```
- A dedicated server install ("7 Days to Die Dedicated Server") only for
  build/install/run/package/verify-reproducible: the mod compiles against the
  game's shipped DLLs, which this repo does not redistribute

## First run

```bash
git clone <this repo> && cd 7dtd-server-optimizer
make test        # full gate, ~70s; needs network once for the pinned NuGet restore
```

`make test` does not need the game installed. `make build` does.

## The edit-test loop

`make test` is the three targets below in sequence, and it is what CI runs. Run
only the one your edit touches; each needs just its own tools.

| Target | Covers | Cost on a warm tree | Needs |
|---|---|---|---|
| `make lint` | `scripts/*.sh` and `scripts/*.py` | seconds | shellcheck, ruff, mypy |
| `make unit` | `Config.Load/Normalize` and the game-type-free C# modules | ~1 min (builds) | .NET SDK |
| `make check-scripts` | config doc/version consistency, `repo_root`, cfg guard, APM/animstate parsers, config backup, coverage badge | ~2s | python3 |

```bash
$EDITOR Source/EfficientServer/Config.cs   # then: make unit
$EDITOR scripts/es_cfg_guard.py            # then: make lint check-scripts
make test                                  # before opening the PR; same gates as CI
```

Run `make test` itself before pushing: it is the only thing that proves the
three agree with each other.

### One check at a time

`make unit` runs the whole C# harness. While chasing a single failure, select
the check by its name instead. The pattern is a case-insensitive `*` glob
against the check's own description, so a word selects every check mentioning
it and a pair of stars narrows a family. Quote it, or the shell eats the stars.

```bash
make unit-list                          # every check name, one per line
make unit FILTER='*endpoints*'          # only the matching checks
make unit FILTER=Governor               # unquoted is fine: no wildcard
```

A pattern that matches nothing exits 1 and says so, instead of reporting a
clean run. `make unit-list` always exits 0 and asserts nothing. Neither is used
by `make test` or CI, which still run the full suite.

For game-facing changes, rebuild against your dedicated install and follow the
evidence loop in [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md): one feature
group at a time, baseline loadgen/APM capture, change, re-measure, gameplay
soak. Performance claims require that evidence; lower CPU alone is not
acceptance (`docs/FEATURES.md` fidelity checks).

## What `make test` checks, and how to fix a failure

| Gate | Target | Fails when | Fix |
|---|---|---|---|
| `shellcheck -x scripts/*.sh` | `make lint` | a shell script has a lint violation | fix the script |
| `ruff check scripts/` (config in `ruff.toml`) | `make lint` | a Python script has a lint violation (undefined name, unused binding, over-long line, import order) | fix the script; rule groups are added only once the tree passes them |
| `mypy scripts/` (config in `mypy.ini`) | `make lint` | a Python script fails type checking (missing/contradictory annotations, unreachable code) | fix the annotations or the code they contradict; stricter flags are added only once the tree passes them |
| `ruff format --check scripts/` | `make lint` | a Python script is not in `ruff format` output | run `ruff format scripts/` and commit the result; the formatter is the layout authority, so do not hand-tune the wrapping it rewrites |
| `python3 -m compileall scripts` | `make check-scripts` | a script has a syntax error | fix the script |
| `dotnet restore --locked-mode` | `make unit` (both package graphs) | you changed a `PackageReference` without regenerating the lockfile | run plain `dotnet restore Source/EfficientServer.Tests` or `dotnet restore Source/EfficientServer` and commit the regenerated `packages.lock.json` with the csproj change |
| config harness (`Source/EfficientServer.Tests`) | `make unit` | a `Config.cs` behavior change broke a pinned check | change the code or update the check together; never delete a check to pass |
| `scripts/check_config_doc.py` (+ `--selftest`) | `make check-scripts` | a `ServerPerfConfig` field exists but is not documented in `docs/CONFIG.md`, `config/efficientserver.json` has keys absent from `Config.cs`, shipped values drift from code defaults, or the gate's own parsing broke | document the field (mechanism, gameplay impact, measured gain), fix the key typo, or fix the script; its selftest is the spec |
| `scripts/check_version.py` (+ `--selftest`) | `make check-scripts` | versions disagree across `ModInfo.xml` / `AssemblyInfo.cs`, docs claim a version newer than shipped, the changelog lacks the shipped version, the `docs/RESULTS.md` version history stops short of it, `SECURITY.md` does not name it as supported, or the gate's own parsing broke | bump `Source/EfficientServer/ModInfo.xml` and `AssemblyInfo.cs` together and add the matching `CHANGELOG.md` entry in the same change, or fix the script; its selftest is the spec |
| `scripts/repo_root.py --selftest` | `make check-scripts` | the repository-root marker walk the other scripts resolve their paths with broke | fix the script, or add the moved/renamed marker to `MARKERS`; its selftest is the spec |
| `scripts/es_cfg_guard.py --selftest` | `make check-scripts` | the config backup/restore guard broke its own protocol | fix the script; its selftest is the spec |
| `scripts/bench_parse.py --selftest` | `make check-scripts` | the APM health-line reader or the `es animstate` parser broke, or the incremental reader stopped agreeing with a full-file rescan | fix the script; its selftest is the spec |
| `scripts/backup_config.py --selftest` | `make check-scripts` | the snapshot / verify / restore protocol broke | fix the script; its selftest is the spec |
| `scripts/apm_tail.py --selftest` | `make check-scripts` | the APM log tail cache or its window math broke, or it stopped invalidating on a log that was truncated, rewritten or rolled over at the same path | fix the script; its selftest is the spec |
| `scripts/coverage_badge.py --selftest` | `make check-scripts` | the coverage-badge generator broke (Cobertura parsing or SVG rendering; CI uses it to publish the README badge) | fix the script; its selftest is the spec |

## PR expectations beyond the gates

- One feature group per change, then re-measure (see above).
- Rebuild and revalidate Harmony targets after every Steam game update.
- Repo conventions live in [`AGENTS.md`](AGENTS.md) and apply to all changes:
  fail soft per patch group, no host topology inside the DLL, no game IL
  redistribution.
- Generated files: never hand-edit `packages.lock.json`; regenerate it with
  the package manager as shown in the table above.

## Where things live

| Path | Role |
|---|---|
| `Source/EfficientServer/` | The mod (Harmony patches, config) |
| `Source/EfficientServer.Tests/` | Self-check harness run by `make test` |
| `scripts/` | Build/install/package tooling and regression gates |
| `config/efficientserver.json` | Shipped default config |
| `docs/DEVELOPMENT.md` | Full workflow, env vars, release process |

Stock-game research belongs in the sibling `7dtd-engine-research` repo, not here
(see `AGENTS.md`).
