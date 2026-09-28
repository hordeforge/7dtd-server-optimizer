# Security policy

Scope: this repository's EfficientServer mod only. The game itself, the
dedicated server binary, and sibling workspace repositories are out of scope;
report those to their respective maintainers (The Fun Pimps for the game).

## Supported versions

Security-relevant fixes are made for the current mod version stated in
`Source/EfficientServer/ModInfo.xml` (1.19.0 at this writing; it is
authoritative) and land on `main`. Older releases receive no backports. The
mod version is independent of the GitHub release tag, so
`EfficientServer-v0.1.0.zip` logging `mod=1.17.0` is correct, not drift.

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
  for every later reload.
- Bench-only toggles are opt-in, then unguarded. Arming `es benchgod on`
  (all players damage-immune until restart) or the `es animoff` / `es rigoff`
  fidelity probes is refused unless `Diagnostics.AllowBenchGod` or
  `Diagnostics.AllowFidelityProbes` is true in that config file; disarming is
  never gated. With the opt-in on, nothing in code further scopes or confirms
  the arm on a live server. The same file that gates them is the unsigned
  config above, so config write access pre-authorizes them;
  see `docs/THREAT_MODEL.md` R3 and R4.

## Supply chain

What ships and how it is protected:

- The mod bundles no third-party code. Every game DLL reference
  (`Assembly-CSharp`, `0Harmony`, `Newtonsoft.Json`, Unity modules, and so on)
  is resolved from the dedicated server's own `Managed/` directory with
  `Private=false`; the zip contains only `EfficientServer.dll`,
  `ModInfo.xml`, the default config, and the MIT license text.
- The single NuGet dependency (`Newtonsoft.Json` for the test harness) is
  exact-pinned in the csproj, hash-pinned in a committed
  `packages.lock.json`, and restored with `dotnet restore --locked-mode` by
  `make test`, so a changed dependency fails instead of floating. Restore
  sources are pinned in-repo by `NuGet.config` (nuget.org only, with inherited
  machine- and user-level feeds cleared), so a feed added outside this repo
  cannot satisfy the package.
- The `dotnet-coverage` local tool is the one fetch that is not hash-locked:
  `.config/dotnet-tools.json` pins its version, but the .NET 8 SDK this repo
  pins has no tool lock file, so `make coverage` resolves that tool's
  transitive graph from nuget.org at run time. It is CI-only and never touches
  a shipped artifact. Dependabot watches it weekly
  (`.github/dependabot.yml`), the same as the packages above.
- Packaging is reproducible (`make verify-reproducible`);
  `SOURCE_DATE_EPOCH` normalizes timestamps so two builds of the same tree
  zip byte-identically.
- CI actions are pinned to commit SHAs (not mutable tags) and the workflow
  token is read-only.

The full model, including entry points, trust boundaries, assets, threats per
boundary, and ranked gaps: [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).
