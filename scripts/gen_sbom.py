#!/usr/bin/env python3
"""Deterministic CycloneDX 1.5 SBOM generator for release zips.

Emits the supply-chain inventory shipped as EfficientServer/bom.json in every
`make package` zip. Inputs are only values already pinned in-tree (the
committed packages.lock.json graph, the release version, the git commit, and
SOURCE_DATE_EPOCH), so the same tree always produces byte-identical output and
stays inside the reproducible-build guarantee enforced by
scripts/verify_reproducible.sh.

The mod bundles no third-party code (every game DLL reference is Private=false;
the game host provides them at runtime). The one external component,
Newtonsoft.Json, is therefore listed as a required runtime dependency with its
NuGet content hash, not as packaged software.

Run: python3 gen_sbom.py --version V --commit SHA --epoch N --lock LOCK --out FILE
     python3 gen_sbom.py --selftest          (wired into `make test`)
"""
from __future__ import annotations

import argparse
import base64
import binascii
import contextlib
import hashlib
import io
import json
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import NoReturn

BOM_FORMAT = "CycloneDX"
SPEC_VERSION = "1.5"
ROOT_REF = "root"


def fail(msg: str) -> NoReturn:
    print(f"gen_sbom.py: {msg}", file=sys.stderr)
    raise SystemExit(2)


def content_hash_hex(content_hash: str) -> tuple[str, str]:
    """NuGet contentHash (base64 digest) to (CycloneDX alg, lowercase hex).

    NuGet writes SHA-256 for some sources and SHA-512 for nuget.org packages;
    the digest length decides, so a wrong label cannot be baked in here.
    """
    raw = base64.b64decode(content_hash)
    algs = {32: "SHA-256", 48: "SHA-384", 64: "SHA-512"}
    if len(raw) not in algs:
        fail(f"unsupported contentHash digest length {len(raw)}")
    return algs[len(raw)], binascii.hexlify(raw).decode("ascii")


def load_components(lock_path: Path) -> list[dict]:
    try:
        lock = json.loads(lock_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as ex:
        fail(f"cannot read lock file {lock_path}: {ex}")
    sections = lock.get("dependencies")
    if not isinstance(sections, dict):
        fail(f"{lock_path}: no dependencies section")

    seen: dict[tuple[str, str], dict] = {}
    for tfm, packages in sorted(sections.items()):
        if not isinstance(packages, dict):
            fail(f"{lock_path}: bad dependency group {tfm}")
        for name, info in sorted(packages.items()):
            if not isinstance(info, dict) or info.get("type") == "Project":
                continue
            resolved = info.get("resolved")
            content_hash = info.get("contentHash")
            if not resolved or not content_hash:
                fail(f"{lock_path}: {name} missing resolved version or contentHash")
            key = (name.lower(), resolved)
            if key in seen:
                continue
            alg, hex_digest = content_hash_hex(content_hash)
            seen[key] = {
                "type": "library",
                "bom-ref": f"pkg:nuget/{name.lower()}@{resolved}",
                "name": name,
                "version": resolved,
                "hashes": [{"alg": alg, "content": hex_digest}],
                "scope": "required",
                "properties": [
                    {
                        # Not shipped in the zip; the dedicated server's Managed
                        # directory provides it at load time.
                        "name": "efficientserver:bundled",
                        "value": "false",
                    }
                ],
            }

    return [seen[k] for k in sorted(seen)]


def build_bom(version: str, commit: str, epoch: int, lock_path: Path) -> dict:
    components = load_components(lock_path)
    purls = [c["bom-ref"] for c in components]
    serial = uuid.uuid5(uuid.NAMESPACE_URL, f"urn:efficientserver:{version}:{commit}")
    return {
        "bomFormat": BOM_FORMAT,
        "specVersion": SPEC_VERSION,
        "serialNumber": f"urn:uuid:{serial}",
        "version": 1,
        "metadata": {
            "timestamp": datetime.fromtimestamp(epoch, tz=timezone.utc).strftime(
                "%Y-%m-%dT%H:%M:%SZ"
            ),
            "component": {
                "bom-ref": ROOT_REF,
                "type": "application",
                "name": "EfficientServer",
                "version": version,
                "description": (
                    "Harmony optimization mod for 7 Days to Die dedicated servers."
                ),
            },
            "properties": [
                {"name": "efficientserver:git_commit", "value": commit},
                {"name": "efficientserver:source_date_epoch", "value": str(epoch)},
            ],
        },
        "components": components,
        "dependencies": [{"ref": ROOT_REF, "dependsOn": purls}],
    }


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(add_help=True, description=__doc__.splitlines()[0])
    ap.add_argument("--version", required=True, help="release version stamped into the BOM")
    ap.add_argument("--commit", required=True, help="git commit the artifact was built from")
    ap.add_argument("--epoch", required=True, type=int, help="SOURCE_DATE_EPOCH value")
    ap.add_argument("--lock", required=True, type=Path, help="packages.lock.json to inventory")
    ap.add_argument("--out", type=Path, help="write JSON here instead of stdout")
    args = ap.parse_args(argv)

    bom = build_bom(args.version, args.commit, args.epoch, args.lock)
    text = json.dumps(bom, indent=2, sort_keys=False) + "\n"
    if args.out is not None:
        args.out.write_text(text, encoding="utf-8")
    else:
        sys.stdout.write(text)
    return 0


def selftest() -> int:
    import tempfile

    ok = True

    def check(cond: bool, what: str) -> None:
        nonlocal ok
        if cond:
            print(f"PASS: {what}")
        else:
            ok = False
            # Same convention as the other gates: failures go to stderr so a
            # stderr-only capture still sees them.
            print(f"FAIL: {what}", file=sys.stderr)

    check(
        content_hash_hex("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=")
        == ("SHA-256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
        "sha256('abc') decodes to the (SHA-256, hex) CycloneDX form",
    )
    sha512_abc_b64 = base64.b64encode(hashlib.sha512(b"abc").digest()).decode()
    check(
        content_hash_hex(sha512_abc_b64)[0] == "SHA-512"
        and len(content_hash_hex(sha512_abc_b64)[1]) == 128,
        "a 64-byte digest is labelled SHA-512",
    )

    lock_body = {
        "version": 1,
        "dependencies": {
            "net8.0": {
                "Newtonsoft.Json": {
                    "type": "Direct",
                    "requested": "[13.0.3, )",
                    "resolved": "13.0.3",
                    "contentHash": "ungn34Dxz6pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=",
                }
            }
        },
    }
    with tempfile.TemporaryDirectory(prefix="es-sbom-test.") as td:
        lock_file = Path(td) / "packages.lock.json"
        lock_file.write_text(json.dumps(lock_body), encoding="utf-8")
        out_a = Path(td) / "a.json"
        out_b = Path(td) / "b.json"

        main(["--version", "1.2.3", "--commit", "deadbeef", "--epoch", "1700000000",
              "--lock", str(lock_file), "--out", str(out_a)])
        main(["--version", "1.2.3", "--commit", "deadbeef", "--epoch", "1700000000",
              "--lock", str(lock_file), "--out", str(out_b)])
        a, b = out_a.read_text(encoding="utf-8"), out_b.read_text(encoding="utf-8")
        check(a == b, "identical inputs produce byte-identical output")

        bom = json.loads(a)
        for key in ("bomFormat", "specVersion", "serialNumber", "metadata",
                    "components", "dependencies"):
            check(key in bom, f"BOM has top-level '{key}'")
        check(bom["bomFormat"] == "CycloneDX" and bom["specVersion"] == "1.5",
              "BOM identifies as CycloneDX 1.5")
        comp = bom["components"][0]
        check(comp["bom-ref"] == "pkg:nuget/newtonsoft.json@13.0.3"
              and comp["hashes"][0]["alg"] in ("SHA-256", "SHA-512"),
              "component carries purl ref and a labeled digest")
        check(bom["dependencies"][0]["dependsOn"] == [comp["bom-ref"]],
              "root dependency edge points at the inventoried component")
        check(bom["serialNumber"].startswith("urn:uuid:"), "serialNumber is a UUID urn")

        out_c = Path(td) / "c.json"
        main(["--version", "1.2.3", "--commit", "deadbeef", "--epoch", "1700000001",
              "--lock", str(lock_file), "--out", str(out_c)])
        c = json.loads(out_c.read_text(encoding="utf-8"))
        check(c["serialNumber"] == bom["serialNumber"],
              "serialNumber depends on version+commit, not epoch")
        check(c["metadata"]["timestamp"] != bom["metadata"]["timestamp"],
              "epoch change moves the timestamp")

        empty_lock = Path(td) / "empty.lock.json"
        empty_lock.write_text(json.dumps({"version": 1, "dependencies": {}}),
                              encoding="utf-8")
        out_d = Path(td) / "d.json"
        main(["--version", "0.0.0", "--commit", "x", "--epoch", "0",
              "--lock", str(empty_lock), "--out", str(out_d)])
        d = json.loads(out_d.read_text(encoding="utf-8"))
        check(d["components"] == [] and d["dependencies"][0]["dependsOn"] == [],
              "empty dependency graph yields an empty but valid BOM")

        broken_lock = Path(td) / "broken.lock.json"
        broken_lock.write_text("{not json", encoding="utf-8")
        out_e = Path(td) / "e.json"
        rc = main_exit_code([
            "--version", "0.0.0", "--commit", "x", "--epoch", "0",
            "--lock", str(broken_lock), "--out", str(out_e)])
        check(rc == 2, f"malformed lock file exits 2 (got {rc})")

        # The same package resolved in several target-framework groups must
        # appear ONCE in the component list (keyed by name+resolved version),
        # or every multi-target lock file would ship duplicate SBOM entries.
        multi_tfm_lock = Path(td) / "multi.lock.json"
        multi_tfm_lock.write_text(json.dumps({
            "version": 1,
            "dependencies": {
                "net8.0": {
                    "Newtonsoft.Json": {
                        "type": "Direct",
                        "requested": "[13.0.3, )",
                        "resolved": "13.0.3",
                        "contentHash": "ungn34Dxz6pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=",
                    }
                },
                "net48": {
                    "Newtonsoft.Json": {
                        "type": "Direct",
                        "requested": "[13.0.3, )",
                        "resolved": "13.0.3",
                        "contentHash": "ungn34Dxz6pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=",
                    }
                },
            },
        }), encoding="utf-8")
        bom_multi = build_bom("t", "t", 0, multi_tfm_lock)
        check(len(bom_multi["components"]) == 1,
              f"package in two TFM groups dedupes to one component "
              f"(got {len(bom_multi['components'])})")

    real_lock = Path(__file__).resolve().parent.parent / (
        "Source/EfficientServer.Tests/packages.lock.json")
    if real_lock.exists():
        bom = build_bom("t", "t", 0, real_lock)
        check(len(bom["components"]) >= 1, "in-repo lock file inventories >= 1 component")
        hexes = [c["hashes"][0]["content"] for c in bom["components"]]
        check(all(len(h) in (64, 128) and all(ch in "0123456789abcdef" for ch in h)
                  for h in hexes),
              "in-repo component hashes decode to lowercase hex digests")

    if ok:
        print("PASS: gen_sbom selftest")
        return 0
    print("FAIL: gen_sbom selftest", file=sys.stderr)
    return 1


def main_exit_code(args: list[str]) -> int:
    """Run main() in-process and capture its exit code (incl. argparse/fail)."""
    buf = io.StringIO()
    try:
        with contextlib.redirect_stderr(buf):
            main(args)
    except SystemExit as ex:
        return ex.code if isinstance(ex.code, int) else 1
    return 0


if __name__ == "__main__":
    argv = sys.argv[1:]
    if argv == ["--selftest"]:
        sys.exit(selftest())
    sys.exit(main(argv))
