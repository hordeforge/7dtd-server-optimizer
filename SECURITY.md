# Security policy

Scope: this repository's EfficientServer mod only. The game itself, the
dedicated server binary, and sibling workspace repositories are out of scope;
report those to their respective maintainers (The Fun Pimps for the game).

## Supported versions

Security-relevant fixes are made against the latest tagged release
(1.17.x at this writing; see `Source/EfficientServer/ModInfo.xml`) and land on
`main`. Older releases receive no backports.

## Reporting

Open a GitHub Issue at https://github.com/maci0/7dtd-optimizer/issues and
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

The full model, including entry points, trust boundaries, assets, threats per
boundary, and ranked gaps: [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).
