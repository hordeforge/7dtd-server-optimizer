# Security policy

Scope: this repository's EfficientServer mod only. The game itself, the
dedicated server binary, and sibling workspace repositories are out of scope;
report those to their respective maintainers (The Fun Pimps for the game).

## Supported versions

Security-relevant fixes are made for the current mod version stated in
`Source/EfficientServer/ModInfo.xml` (1.20.0 at this writing; it is
authoritative) and land on `main`. Older releases receive no backports, so an
operator on an older mod version gets no fix, security or otherwise. The mod
version is independent of the GitHub release tag, so
`EfficientServer-0.1.0.zip` logging `mod=1.17.0` is correct, not drift.

## Reporting

Open a GitHub Issue at https://github.com/hordeforge/7dtd-server-optimizer/issues and
include: affected version, game build, the relevant log lines
(`[EfficientServer]`-prefixed), and the config that reproduces it. There is no
private disclosure channel published yet; do not include exploit details you
are not comfortable posting publicly until one exists.

## Security properties of this mod, stated plainly

Read these before deploying; they are properties of the architecture, not
vulnerabilities:

- In-process authority. The mod is a Harmony DLL loaded by the dedicated
  server and runs with the server's full privileges. It can read and affect
  anything the server process can. Treat a compromised or tampered
  `EfficientServer.dll` as full server compromise.
- EAC must be off. Loading any C# mod forces EasyAntiCheat off for the server
  (`docs/FEATURES.md`, "Anti-cheat"). Client-side cheat protection is absent
  on deployments of this mod; compensate with server-side admin practices.
- No network surface of its own. The mod opens no sockets and adds no
  endpoints. Remote attack surface comes from the game (game port, telnet,
  web dashboard) as configured in your `serverconfig.xml`; keep telnet on its
  loopback fallback or behind a firewall.
- Operator-trusted config. `Config/efficientserver.json` under the installed
  mod folder is parsed with Newtonsoft.Json, clamped to safe ranges
  (`Normalize` in `Config.cs`), and falls back to defaults on malformed input.
  It is read unsigned and is re-read on every `es reload`, so anyone who can
  write that file can reshape gameplay and load behavior within clamped bounds
  for every later reload. A reload whose file fails to parse is rejected and
  the previous config stays live, so a broken edit does not quietly revert
  every tuned knob to its default. The same write position is held by two tools
  in this repository: `scripts/es_cfg_guard.py` rewrites managed keys of the
  installed config in place, and `scripts/backup_config.py` copies the live
  config off-host and can restore over it (`--force` required to overwrite).
- Bench-only toggles are opt-in, then largely unguarded. Arming `es benchgod
  on` (all players damage-immune until restart) or the `es animoff` / `es
  rigoff` fidelity probes is refused unless `Diagnostics.AllowBenchGod` or
  `Diagnostics.AllowFidelityProbes` is true in that config file; disarming is
  never gated. Nothing at arming time further scopes the arm or asks for
  confirmation on a live server. What the code does do is stop it: the damage
  prefix re-reads the allow-switch on every damage event, and an `es reload`
  that no longer allows the toggles releases both the damage-immunity latch and
  any armed animator/rig probes, so revoking the opt-in is the undo. The same
  file that gates them is the unsigned config above, so config write access
  pre-authorizes them; see `docs/THREAT_MODEL.md` R3 and R4.

## Supply chain

What ships and how it is protected:

- The mod bundles no third-party code. Every game DLL reference
  (`Assembly-CSharp`, `0Harmony`, `Newtonsoft.Json`, Unity modules, and so on)
  is resolved from the dedicated server's own `Managed/` directory with
  `Private=false`; the zip contains only `EfficientServer.dll`,
  `ModInfo.xml`, the default config, and the MIT license text.
- Two package graphs are fetched, three packages in total. The test harness's
  `Newtonsoft.Json` is
  exact-pinned in its csproj, hash-pinned in a committed
  `Source/EfficientServer.Tests/packages.lock.json`, and restored with
  `dotnet restore --locked-mode` by `make test`, so a changed dependency fails
  instead of floating. The mod project fetches two more,
  `Microsoft.NETFramework.ReferenceAssemblies` and its `.net48` leaf, both
  exact-pinned as `[1.0.3]` in
  `Source/EfficientServer/EfficientServer.csproj`; declaring the parent there
  overrides the SDK's own implicit reference, which it would otherwise add
  with an open `[1.0.3, )` range. Those packages are
  reference metadata only (`PrivateAssets="all"`), so they cannot change the
  emitted IL. Their graph is hash-pinned too: a committed
  `Source/EfficientServer/packages.lock.json` records the content hash of both.
  The hash is enforced: `make build` and `make test` both restore the mod
  project with `--locked-mode`, so a lock file that drifts fails the gate
  instead of being rewritten in place. Restore sources are pinned in-repo by
  `NuGet.config` (nuget.org only, with inherited machine- and user-level feeds
  cleared), so a feed added outside this repo cannot satisfy either package.
- The `dotnet-coverage` local tool is a third fetch that is not hash-locked:
  `.config/dotnet-tools.json` pins its version, but the .NET 8 SDK this repo
  pins has no tool lock file, so `make coverage` resolves that tool's
  transitive graph from nuget.org at run time. It is CI-only and never touches
  a shipped artifact. Dependabot watches it weekly
  (`.github/dependabot.yml`), the same as the packages above.
- The lint toolchain is fetched too, and is the one surface with no hash and
  no automated watch: the CI optimizer job installs ruff and mypy from PyPI at
  the exact versions in the Makefile (`RUFF_VERSION`, `MYPY_VERSION`), but
  `uv tool install` resolves their transitive dependencies at run time and no
  hashed requirements file is committed. They gate the shipped source only,
  and `make test` rejects any version other than the pinned one.
- Packaging is reproducible (`make verify-reproducible`);
  `SOURCE_DATE_EPOCH` normalizes timestamps so two builds of the same tree
  zip byte-identically.
- CI actions are pinned to commit SHAs (not mutable tags) and the workflow
  token is read-only, in both `.github/workflows/ci.yml` and the tag-driven
  `.github/workflows/release.yml`. Neither workflow builds or publishes the
  mod archive: `make package` needs the dedicated server's own assemblies,
  which a hosted runner does not have, so a maintainer builds and attaches it.
  The release workflow checks only that a `v*` tag agrees with the version in
  `Source/EfficientServer/ModInfo.xml` and with the changelog.

The full model, including entry points, trust boundaries, assets, threats per
boundary, and ranked gaps: [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).
