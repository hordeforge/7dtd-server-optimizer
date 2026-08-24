# Contributing to 7dtd-server-optimizer

The whole CI gate is one local command. If `make test` passes on your machine,
CI will pass: it runs exactly `make test` on every PR and on pushes to main
(`.github/workflows/ci.yml`).

## Requirements

- Linux host (scripts assume Steam library paths, GNU coreutils, `taskset`)
- .NET SDK, 8.0.4xx band, pinned by [`global.json`](global.json). The Makefile
  picks up a local install from `~/.cache/dotnet-sdk` or `~/.dotnet`
  automatically; otherwise put `dotnet` on `PATH`
- `shellcheck`, `ruff`, `mypy`, and Python 3 (`make test`)
- A dedicated server install ("7 Days to Die Dedicated Server") only for
  build/install/run/package: the mod compiles against the game's shipped DLLs,
  which this repo does not redistribute

## First run

```bash
git clone <this repo> && cd 7dtd-server-optimizer
make test        # ~2s; needs network once for the pinned NuGet restore
```

`make test` does not need the game installed. `make build` does.

## The edit-test loop

```bash
$EDITOR Source/EfficientServer/Config.cs   # or patches, or scripts/
make test                                  # ~2s, same gates as CI
```

For game-facing changes, rebuild against your dedicated install and follow the
evidence loop in [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md): one feature
group at a time, baseline loadgen/APM capture, change, re-measure, gameplay
soak. Performance claims require that evidence; lower CPU alone is not
acceptance (`docs/FEATURES.md` fidelity checks).

## What `make test` checks, and how to fix a failure

| Gate | Fails when | Fix |
|---|---|---|
| `shellcheck -x scripts/*.sh` | a shell script has a lint violation | fix the script |
| `ruff check scripts/` (config in `ruff.toml`) | a Python script has a lint violation (undefined name, unused binding, over-long line, import order) | fix the script; rule groups are added only once the tree passes them |
| `mypy scripts/` (config in `mypy.ini`) | a Python script fails type checking (missing/contradictory annotations, unreachable code) | fix the annotations or the code they contradict; stricter flags are added only once the tree passes them |
| `python3 -m compileall scripts` | a script has a syntax error | fix the script |
| `dotnet restore --locked-mode` | you changed a `PackageReference` without regenerating the lockfile | run plain `dotnet restore Source/EfficientServer.Tests` and commit the regenerated `packages.lock.json` with the csproj change |
| config harness (`Source/EfficientServer.Tests`) | a `Config.cs` behavior change broke a pinned check | change the code or update the check together; never delete a check to pass |
| `scripts/check_config_doc.py` (+ `--selftest`) | a `ServerPerfConfig` field exists but is not documented in `docs/CONFIG.md`, `config/efficientserver.json` has keys absent from `Config.cs`, shipped values drift from code defaults, or the gate's own parsing broke | document the field (mechanism, gameplay impact, measured gain), fix the key typo, or fix the script; its selftest is the spec |
| `scripts/check_version.py` (+ `--selftest`) | versions disagree across `ModInfo.xml` / `AssemblyInfo.cs`, docs claim a version newer than shipped, the changelog lacks the shipped version, or the gate's own parsing broke | bump `Source/EfficientServer/ModInfo.xml` and `AssemblyInfo.cs` together and add the matching `CHANGELOG.md` entry in the same change, or fix the script; its selftest is the spec |
| `scripts/es_cfg_guard.py --selftest` | the config backup/restore guard broke its own protocol | fix the script; its selftest is the spec |
| `scripts/gen_sbom.py --selftest` | the SBOM generator broke (determinism, lock parsing, CycloneDX structure) | fix the script; its selftest is the spec |

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
