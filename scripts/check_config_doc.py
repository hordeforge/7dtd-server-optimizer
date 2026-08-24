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

Run: python3 scripts/check_config_doc.py
     python3 scripts/check_config_doc.py --selftest     (both wired into `make test`)
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
CLASS_BLOCK = re.compile(
    r"^ {4}(?:public |internal )?(?:sealed )?class (\w+)\n    \{\n(.*?)^    \}",
    re.MULTILINE | re.DOTALL,
)
PROP_DECL = re.compile(r"public (\w+) (\w+) \{ get; set; \}(?: = ([^;]+);)?")

USAGE = """\
usage: check_config_doc.py [--selftest] [-h | --help]

Gate: every ServerPerfConfig field must be documented in docs/CONFIG.md, every
key in config/efficientserver.json must exist in Config.cs, and every shipped
value must equal the C# built-in default. Wired into `make test`.
  --selftest  exercise the parsing/comparison logic itself (the repo gate above
              only fails when tree and code drift; it stays green if this
              script's own matching logic silently breaks)
  -h, --help  show this help\
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


def _selftest() -> int:
    """Pin the gate's own parsing/comparison contracts with synthetic inputs.

    The repo-facing gate compares tree state against Config.cs; it cannot fail
    when its own regexes or comparators rot (a PROP_DECL that matches nothing
    would report "all 0 fields documented" as OK). These checks use a synthetic
    Config.cs-shaped snippet and plain dicts so every helper's spec is asserted
    directly, following the --selftest convention of es_cfg_guard/gen_sbom.
    """
    failures: list[str] = []

    def check(name: str, cond: bool) -> None:
        if cond:
            print("PASS: " + name)
        else:
            print("FAIL: " + name, file=sys.stderr)
            failures.append(name)

    # parse_cs_default: every initializer form Config.cs uses.
    check(
        "parse_cs_default bool literals",
        parse_cs_default("true") is True and parse_cs_default("false") is False,
    )
    check(
        "parse_cs_default int literals",
        parse_cs_default("4") == 4 and parse_cs_default("-1") == -1,
    )
    check(
        "parse_cs_default float suffix stripped to number",
        parse_cs_default("100f") == 100 and parse_cs_default("0.5f") == 0.5,
    )
    check("parse_cs_default absent initializer -> None", parse_cs_default(None) is None)
    check(
        "parse_cs_default non-numeric literal -> None (skipped, not misreported as drift)",
        parse_cs_default('"AiLod"') is None,
    )

    # values_equal: Python bool IS int, so the type-faithful guard is the spec.
    check(
        "values_equal never equates bool with number",
        not values_equal(True, 1) and not values_equal(False, 0) and not values_equal(1, True),
    )
    check(
        "values_equal bool identity holds",
        values_equal(True, True) and values_equal(False, False),
    )
    check("values_equal accepts int/float cross-type", values_equal(100, 100.0))
    check(
        "values_equal detects real drift",
        not values_equal(2, 3) and not values_equal(True, False),
    )
    check(
        "values_equal strings compare exactly",
        values_equal("a", "a") and not values_equal("a", "b"),
    )

    # parse_cs_schema: class extraction, scalar/section classification, and the
    # `{ get; set; }` anchor that keeps method declarations out of the schema.
    cs_snippet = (
        "namespace EfficientServer\n"
        "{\n"
        "    public sealed class AiLodConfig\n"
        "    {\n"
        "        public bool Enabled { get; set; } = true;\n"
        "        public float FullAiDistSq { get; set; } = 100f;\n"
        "        public int Stride { get; set; }\n"
        "        public SubConfig Sub { get; set; } = new SubConfig();\n"
        "        public void Reset() { Enabled = false; }\n"
        "    }\n"
        "\n"
        "    public sealed class SubConfig\n"
        "    {\n"
        "        public int Leaf { get; set; } = 7;\n"
        "    }\n"
        "\n"
        "    public sealed class ServerPerfConfig\n"
        "    {\n"
        "        public bool Enabled { get; set; } = true;\n"
        "        public AiLodConfig AiLod { get; set; } = new AiLodConfig();\n"
        "    }\n"
        "}\n"
    )
    schema = parse_cs_schema(cs_snippet)
    check(
        "parse_cs_schema finds every top-level config class",
        set(schema) == {"AiLodConfig", "SubConfig", "ServerPerfConfig"},
    )
    check(
        "parse_cs_schema parses scalar defaults (bool/int/float-suffix/absent)",
        schema["AiLodConfig"]["scalars"]
        == {"Enabled": True, "FullAiDistSq": 100, "Stride": None},
    )
    check(
        "parse_cs_schema classifies non-scalar properties as sections by type name",
        schema["AiLodConfig"]["sections"] == {"Sub": "SubConfig"},
    )
    check(
        "parse_cs_schema ignores methods without { get; set; }",
        all("Reset" not in part for part in schema["AiLodConfig"].values()),
    )

    # unknown_json_keys: typo paths at both levels plus wrong-shape values.
    clean = {"Enabled": False, "AiLod": {"Enabled": True, "FullAiDistSq": 100}}
    check(
        "unknown_json_keys accepts a fully known template",
        unknown_json_keys(schema, clean) == [],
    )
    check(
        "unknown_json_keys names a root-level typo",
        unknown_json_keys(schema, {"Enabld": True}) == ["Enabld"],
    )
    check(
        "unknown_json_keys names a nested typo as a dotted path",
        unknown_json_keys(schema, {"AiLod": {"FullAiDistSqX": 5}}) == ["AiLod.FullAiDistSqX"],
    )
    check(
        "unknown_json_keys flags an object where a scalar belongs",
        unknown_json_keys(schema, {"AiLod": {"Enabled": {}}})
        == ["AiLod.Enabled (object where scalar expected)"],
    )
    check(
        "unknown_json_keys flags a section bound to a non-object value",
        unknown_json_keys(schema, {"AiLod": [1]}) == ["AiLod"],
    )

    # default_drift: exact message format pinned (it is operator-facing output).
    check(
        "default_drift reports scalar drift with both values",
        default_drift(schema, {"Enabled": False})
        == ["Enabled: shipped False != code default True"],
    )
    check(
        "default_drift reports nested drift with dotted path",
        default_drift(schema, {"AiLod": {"FullAiDistSq": 50}})
        == ["AiLod.FullAiDistSq: shipped 50 != code default 100"],
    )
    check(
        "default_drift skips keys absent from the template",
        default_drift(schema, {}) == [] and default_drift(schema, {"AiLod": {}}) == [],
    )
    check(
        "default_drift skips expression defaults it cannot compare",
        default_drift(schema, {"AiLod": {"Stride": 99}}) == [],
    )
    check(
        "default_drift accepts code defaults verbatim",
        default_drift(schema, {"Enabled": True, "AiLod": {"FullAiDistSq": 100}}) == [],
    )

    if failures:
        print(f"FAIL: {len(failures)} check_config_doc selftest check(s)", file=sys.stderr)
        return 1
    print("PASS: check_config_doc selftest")
    return 0


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
        print(
            f"FAIL: {len(missing)} config field(s) undocumented in {CONFIG_MD.name}:",
            file=sys.stderr,
        )
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
        print(
            f"FAIL: {len(problems)} unknown key(s) in {CONFIG_JSON.relative_to(ROOT)}:",
            file=sys.stderr,
        )
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
    if argv == ["--selftest"]:
        raise SystemExit(_selftest())
    if argv:
        print(f"check_config_doc.py: unrecognized arguments: {' '.join(argv)}", file=sys.stderr)
        print(USAGE, file=sys.stderr)
        raise SystemExit(2)
    sys.exit(main())
