#!/usr/bin/env python3
"""Regression gate: every ServerPerfConfig field must be documented in CONFIG.md,
and every key in the shipped config/efficientserver.json must exist in Config.cs.

Cross-checks the property names declared in Source/EfficientServer/Config.cs
against the backticked identifiers used in docs/CONFIG.md. A field is "covered"
when its bare name appears in a doc token (e.g. `ResolveEveryNTicks` or
`CrowdCollisionLod.ResolveEveryNTicks`). Then walks the shipped JSON template
against the C# property schema so a misspelled knob cannot ship (the mod logs
unknown keys at load, this gate catches them before packaging).

Run: python3 scripts/check_config_doc.py   (wired into `make test`)
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONFIG_CS = ROOT / "Source" / "EfficientServer" / "Config.cs"
CONFIG_MD = ROOT / "docs" / "CONFIG.md"
CONFIG_JSON = ROOT / "config" / "efficientserver.json"

# Names that are methods, not config fields (skip).
NON_FIELDS = {"FeatureActive", "ShouldRunFor", "Normalize", "Load", "DefaultPathBesideAssembly"}

SCALAR_TYPES = {"bool", "int", "float", "string", "double", "long"}
# One top-level class block of Config.cs (bodies are 4-space indented).
CLASS_BLOCK = re.compile(r"^ {4}(?:public |internal )?(?:sealed )?class (\w+)\n    \{\n(.*?)^    \}", re.M | re.S)
PROP_DECL = re.compile(r"public (\w+) (\w+) \{ get; set; \}")

USAGE = """\
usage: check_config_doc.py [-h | --help]

Gate: every ServerPerfConfig field must be documented in docs/CONFIG.md and
every key in config/efficientserver.json must exist in Config.cs. Wired into
`make test`. Takes no options besides -h/--help.\
"""


def parse_cs_schema(src: str) -> dict[str, dict]:
    """class name -> {'scalars': set(names), 'sections': {name: class type}}."""
    schema: dict[str, dict] = {}
    for cls, body in CLASS_BLOCK.findall(src):
        scalars = {p for t, p in PROP_DECL.findall(body) if t in SCALAR_TYPES}
        sections = {p: t for t, p in PROP_DECL.findall(body) if t not in SCALAR_TYPES}
        schema[cls] = {"scalars": scalars, "sections": sections}
    return schema


def unknown_json_keys(schema: dict[str, dict], data: dict) -> list[str]:
    """Dotted paths in the shipped JSON that match no Config.cs property."""
    problems: list[str] = []
    root = schema["ServerPerfConfig"]
    for key, value in data.items():
        if key in root["scalars"]:
            continue
        if key in root["sections"] and isinstance(value, dict):
            sub = schema[root["sections"][key]]
            for sub_key, sub_value in value.items():
                if sub_key in sub["scalars"] or sub_key in sub["sections"]:
                    if isinstance(sub_value, dict):
                        problems.append(f"{key}.{sub_key} (object where scalar expected)")
                else:
                    problems.append(f"{key}.{sub_key}")
            continue
        problems.append(key)
    return problems


def main() -> int:
    src = CONFIG_CS.read_text(encoding="utf-8")
    doc = CONFIG_MD.read_text(encoding="utf-8")

    fields = sorted(
        set(re.findall(r"public (?:bool|int|float|string|double|long) ([A-Za-z]+)", src))
        - NON_FIELDS
    )
    doc_tokens = set(re.findall(r"`([A-Za-z]+(?:\.[A-Za-z]+)?)`", doc))
    doc_names = {t.split(".")[-1] for t in doc_tokens}

    missing = [f for f in fields if f not in doc_names]
    if missing:
        print(f"FAIL: {len(missing)} config field(s) undocumented in {CONFIG_MD.name}:", file=sys.stderr)
        for m in missing:
            print(f"  {m}", file=sys.stderr)
        return 1
    print(f"OK: all {len(fields)} config fields documented in {CONFIG_MD.name}")

    try:
        data = json.loads(CONFIG_JSON.read_text(encoding="utf-8"))
    except (OSError, ValueError) as ex:
        print(f"FAIL: {CONFIG_JSON.name} is not valid JSON: {ex}", file=sys.stderr)
        return 1
    problems = unknown_json_keys(parse_cs_schema(src), data)
    if problems:
        print(f"FAIL: {len(problems)} unknown key(s) in {CONFIG_JSON.relative_to(ROOT)}:", file=sys.stderr)
        for p in problems:
            print(f"  {p}", file=sys.stderr)
        return 1
    print(f"OK: all shipped config keys match Config.cs ({len(data)} top-level)")
    return 0


if __name__ == "__main__":
    argv = sys.argv[1:]
    if argv in (["-h"], ["--help"]):
        print(USAGE)
        raise SystemExit(0)
    if argv:
        print(f"check_config_doc.py: unrecognized arguments: {' '.join(argv)}", file=sys.stderr)
        print(USAGE, file=sys.stderr)
        raise SystemExit(2)
    sys.exit(main())
