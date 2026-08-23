# Security policy

Scope: this repository's EfficientServer mod only. The game itself, the
dedicated server binary, and sibling workspace repositories are out of scope;
report those to their respective maintainers (The Fun Pimps for the game).

## Supported versions

Security-relevant fixes are made for the current release version stated in
`Source/EfficientServer/ModInfo.xml` (1.17.x at this writing; it is
authoritative, git tags lag it) and land on `main`. Older releases receive no
backports.

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
- Operator-trusted config. `Config/efficientserver.json` beside the DLL is
  parsed with Newtonsoft.Json, clamped to safe ranges (`Normalize` in
  `Config.cs`), and falls back to defaults on malformed input. Anyone who can
  write that file can reshape gameplay and load behavior within clamped
  bounds, including enabling diagnostics that intentionally freeze the server.
- Bench-only toggles are not prod-guarded. `es benchgod on` makes all players
  damage-immune until restart. Nothing in code refuses it on a live server;
  see `docs/THREAT_MODEL.md` R3.

## Supply chain

What ships and how it is protected:

- The mod bundles no third-party code. Every game DLL reference
  (`Assembly-CSharp`, `0Harmony`, `Newtonsoft.Json`, Unity modules, and so on)
  is resolved from the dedicated server's own `Managed/` directory with
  `Private=false`; the zip contains only `EfficientServer.dll`,
  `ModInfo.xml`, the default config, and the SBOM below.
- The single NuGet dependency (`Newtonsoft.Json` for the test harness) is
  exact-pinned in the csproj, hash-pinned in a committed
  `packages.lock.json`, and restored with `dotnet restore --locked-mode` by
  `make test`, so a changed dependency fails instead of floating.
- Every release zip carries a deterministic CycloneDX 1.5 SBOM at
  `EfficientServer/bom.json` (generated from that lock file by
  `scripts/gen_sbom.py`), so scanners and deployers can inventory exactly what
  shipped without unpacking assumptions.
- Packaging is reproducible (`make verify-reproducible`) and each run records
  artifact SHA-256, source epoch, commit, and compiler in
  `dist/EfficientServer-*.buildinfo.txt`.
- CI actions are pinned to commit SHAs (not mutable tags) and the workflow
  token is read-only.

The full model, including entry points, trust boundaries, assets, threats per
boundary, and ranked gaps: [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).
