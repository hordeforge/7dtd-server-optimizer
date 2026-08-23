#!/usr/bin/env python3
"""Regression gate: every ServerPerfConfig field must be documented in CONFIG.md,
every key in the shipped config/efficientserver.json must exist in Config.cs,
and every value in the shipped template must equal the C# built-in default.

Cross-checks the property names declared in Source/EfficientServer/Config.cs
against the backticked identifiers used in docs/CONFIG.md. A field is "covered"
when its bare name appears in a doc token (e.g. `ResolveEveryNTicks` or
`CrowdCollisionLod.ResolveEveryNTicks`). Then walks the shipped JSON template
against the C# property schema so a misspelled knob cannot ship (the mod logs
unknown keys at load, this gate catches them before packaging), and compares
each shipped value against the property initializer so the template and the
code defaults cannot drift apart silently (CONFIG.md documents one set of
defaults; two divergent copies would make one of them a lie).

Run: python3 scripts/check_config_doc.py   (wired into `make test`)
"""
from __future__ import annotations

import json
import math
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONFIG_CS = ROOT / "Source" / "EfficientServer" / "Config.cs"
CONFIG_MD = ROOT / "docs" / "CONFIG.md"
CONFIG_JSON = ROOT / "config" / "efficientserver.json"

SCALAR_TYPES = {"bool", "int", "float", "string", "double", "long"}
# One top-level class block of Config.cs (bodies are 4-space indented).
CLASS_BLOCK = re.compile(r"^ {4}(?:public |internal )?(?:sealed )?class (\w+)\n    \{\n(.*?)^    \}", re.M | re.S)
PROP_DECL = re.compile(r"public (\w+) (\w+) \{ get; set; \}(?: = ([^;]+);)?")

USAGE = """\
usage: check_config_doc.py [-h | --help]

Gate: every ServerPerfConfig field must be documented in docs/CONFIG.md, every
key in config/efficientserver.json must exist in Config.cs, and every shipped
value must equal the C# built-in default. Wired into `make test`. Takes no
options besides -h/--help.\
"""


def parse_cs_default(lit: str | None) -> object:
    """Translate a C# property initializer literal to a comparable Python value.

    None when absent or not a plain literal (an expression initializer cannot
    be compared and is skipped rather than misreported as drift).
    """
    if lit is None:
        return None
    s = lit.strip()
    if s == "true":
        return True
    if s == "false":
        return False
    if s.endswith(("f", "F", "m", "M", "d", "D")):
        s = s[:-1]
    try:
        return int(s)
    except ValueError:
        pass
    try:
        return float(s)
    except ValueError:
        return None


def values_equal(cs_val: object, json_val: object) -> bool:
    """Type-faithful comparison; floats tolerate float32-vs-decimal rounding."""
    if isinstance(cs_val, bool) != isinstance(json_val, bool):
        return False
    if isinstance(cs_val, bool):
        return cs_val == json_val
    if isinstance(cs_val, (int, float)) and isinstance(json_val, (int, float)):
        return math.isclose(float(cs_val), float(json_val), rel_tol=1e-6, abs_tol=1e-12)
    return cs_val == json_val


def parse_cs_schema(src: str) -> dict[str, dict]:
    """class name -> {'scalars': {name: default}, 'sections': {name: class type}}."""
    schema: dict[str, dict] = {}
    for cls, body in CLASS_BLOCK.findall(src):
        scalars = {
            p: parse_cs_default(init)
            for t, p, init in PROP_DECL.findall(body)
            if t in SCALAR_TYPES
        }
        sections = {p: t for t, p, _ in PROP_DECL.findall(body) if t not in SCALAR_TYPES}
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


def default_drift(schema: dict[str, dict], data: dict) -> list[str]:
    """Shipped JSON values that differ from the C# built-in defaults.

    Only keys PRESENT in the template are compared (absent keys legitimately
    keep their default at load). A None default means the initializer was not
    a plain literal and comparison is skipped instead of misreported.
    """
    problems: list[str] = []
    root = schema["ServerPerfConfig"]
    for key, value in data.items():
        if key in root["scalars"]:
            dv = root["scalars"][key]
            if dv is not None and not values_equal(dv, value):
                problems.append(f"{key}: shipped {value!r} != code default {dv!r}")
            continue
        if key in root["sections"] and isinstance(value, dict):
            sub = schema[root["sections"][key]]
            for sub_key, sub_value in value.items():
                if sub_key in sub["scalars"]:
                    dv = sub["scalars"][sub_key]
                    if dv is not None and not values_equal(dv, sub_value):
                        problems.append(
                            f"{key}.{sub_key}: shipped {sub_value!r} != code default {dv!r}"
                        )
    return problems


def main() -> int:
    src = CONFIG_CS.read_text(encoding="utf-8")
    doc = CONFIG_MD.read_text(encoding="utf-8")
    schema = parse_cs_schema(src)

    # PROP_DECL anchors on `{ get; set; }`, so methods can never leak in here.
    all_scalars: set[str] = set()
    for parsed in schema.values():
        all_scalars |= parsed["scalars"].keys()
    fields = sorted(all_scalars)
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
    problems = unknown_json_keys(schema, data)
    if problems:
        print(f"FAIL: {len(problems)} unknown key(s) in {CONFIG_JSON.relative_to(ROOT)}:", file=sys.stderr)
        for p in problems:
            print(f"  {p}", file=sys.stderr)
        return 1
    print(f"OK: all shipped config keys match Config.cs ({len(data)} top-level)")

    drift = default_drift(schema, data)
    if drift:
        print(
            f"FAIL: {len(drift)} shipped value(s) differ from the Config.cs defaults:",
            file=sys.stderr,
        )
        for p in drift:
            print(f"  {p}", file=sys.stderr)
        return 1
    print("OK: shipped template values equal the C# built-in defaults")
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
