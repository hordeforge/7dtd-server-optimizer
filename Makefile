ROOT := $(CURDIR)
DS ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server
# Scripts read SEVENDTD_DS_DIR; route the documented `make install DS=...`
# variable through so both spellings work and cannot drift apart.
export SEVENDTD_DS_DIR ?= $(DS)

# Prefer a local SDK if present (cache or ~/.dotnet), like 7dtd-loadgen.
# Probe for the muxer binary, not bare directory existence: ~/.dotnet also
# collects telemetry sentinels from system-wide installs that contain no SDK,
# and exporting such a dir as DOTNET_ROOT would point SDK resolution at an
# empty tree. build.sh applies the same executability check.
DOTNET_ROOT ?= $(firstword \
	$(foreach d,$(HOME)/.cache/dotnet-sdk $(HOME)/.dotnet,\
	  $(if $(wildcard $d/dotnet),$d)) \
	)
ifneq ($(DOTNET_ROOT),)
  export DOTNET_ROOT
  export PATH := $(DOTNET_ROOT):$(PATH)
endif

# Bare `make` must orient a fresh contributor, not fail on a missing game
# install (the old implicit default target, build, did exactly that).
.DEFAULT_GOAL := help

# Exact ruff pin, mirrored by the two `uv tool install ruff==` steps in
# .github/workflows/ci.yml. make test refuses other versions so lint rule
# behavior cannot silently diverge between a green local run and a red
# remote one (or vice versa). Bump = reviewed change of this line plus both
# ci.yml steps plus README.md, like global.json's SDK pin.
RUFF_VERSION := 0.16.4

# Exact mypy pin, mirrored by the `uv tool install mypy==` step in
# .github/workflows/ci.yml. Same rationale as RUFF_VERSION: checker behavior
# diverges between versions, so the type gate must be identical on both sides.
MYPY_VERSION := 2.1.0

.PHONY: help build build-mcs test coverage install uninstall run clean package verify-reproducible
help:
	@echo "EfficientServer: Harmony optimization mod for 7 Days to Die dedicated servers"
	@echo
	@echo "Contributor loop (works without a game install):"
	@echo "  make test              Every CI gate: shellcheck + ruff + mypy +"
	@echo "                         script syntax + config harness + doc/version"
	@echo "  make clean             Remove dist/ and bin/obj build outputs"
	@echo
	@echo "Game-backed targets (need DS=/path/to/'7 Days to Die Dedicated Server',"
	@echo "default: ~/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server):"
	@echo "  make build             Compile dist/EfficientServer against game DLLs"
	@echo "  make build-mcs         Same, forcing the Mono mcs fallback backend"
	@echo "  make install           Build and copy into \$$DS/Mods/EfficientServer"
	@echo "  make uninstall         Remove \$$DS/Mods/EfficientServer"
	@echo "  make run               Launch the dedicated server with tuned env"
	@echo "  make package           Build and zip dist/EfficientServer-<version>.zip"
	@echo "  make verify-reproducible   Package twice, compare hashes (repro proof)"
	@echo
	@echo "Docs: README.md (toolchain), CONTRIBUTING.md (PR gates), docs/DEVELOPMENT.md"
build:
	$(ROOT)/scripts/build.sh
package:
	$(ROOT)/scripts/package.sh
verify-reproducible:
	$(ROOT)/scripts/verify_reproducible.sh
build-mcs:
	SEVENDTD_BUILD_BACKEND=mcs $(ROOT)/scripts/build.sh
# Preflight so a clean machine gets a named error plus the fix instead of a
# bare "No such file or directory" (Error 127) from whichever gate runs first.
# Runs after PATH setup above, so a real SDK under ~/.cache/dotnet-sdk or
# ~/.dotnet counts as found. The second dotnet gate catches runtime-only
# hosts (distro 'dotnet' with zero SDKs) that would otherwise sail past
# `command -v` and die mid-gate inside `dotnet restore` with resolver noise.
test:
	@if ! command -v shellcheck >/dev/null 2>&1; then \
	  echo "ERROR: make test needs shellcheck (lint gate for scripts/*.sh)." >&2; \
	  echo "  Install it (e.g. apt-get install shellcheck) and rerun make test." >&2; exit 127; fi
	@if ! command -v python3 >/dev/null 2>&1; then \
	  echo "ERROR: make test needs python3 (config-doc, version and cfg-guard gates)." >&2; \
	  echo "  Install python3 and rerun make test." >&2; exit 127; fi
	@if ! command -v ruff >/dev/null 2>&1; then \
	  echo "ERROR: make test needs ruff $(RUFF_VERSION) (lint gate for scripts/*.py, config in ruff.toml)." >&2; \
	  echo "  Install the pinned version: uv tool install ruff=$(RUFF_VERSION) and rerun make test." >&2; exit 127; fi
	@if [ "$$(ruff --version 2>/dev/null | awk '{print $$2}')" != "$(RUFF_VERSION)" ]; then \
	  echo "ERROR: make test needs ruff exactly $(RUFF_VERSION), matching .github/workflows/ci.yml; found $$(ruff --version 2>/dev/null)." >&2; \
	  echo "  Rule behavior diverges between versions, so CI and local runs must agree:" >&2; \
	  echo "  uv tool install --force ruff=$(RUFF_VERSION)." >&2; exit 1; fi
	@if ! command -v mypy >/dev/null 2>&1; then \
	  echo "ERROR: make test needs mypy $(MYPY_VERSION) (type gate for scripts/*.py, config in mypy.ini)." >&2; \
	  echo "  Install the pinned version: uv tool install mypy=$(MYPY_VERSION) and rerun make test." >&2; exit 127; fi
	@if [ "$$(mypy --version 2>/dev/null | awk '{print $$2}')" != "$(MYPY_VERSION)" ]; then \
	  echo "ERROR: make test needs mypy exactly $(MYPY_VERSION), matching .github/workflows/ci.yml; found $$(mypy --version 2>/dev/null)." >&2; \
	  echo "  Checker behavior diverges between versions, so CI and local runs must agree:" >&2; \
	  echo "  uv tool install --force mypy=$(MYPY_VERSION)." >&2; exit 1; fi
	@if ! command -v dotnet >/dev/null 2>&1; then \
	  echo "ERROR: make test needs the .NET SDK pinned by global.json (8.0 band), but no dotnet is on PATH." >&2; \
	  echo "  A real SDK install under ~/.cache/dotnet-sdk or ~/.dotnet is picked up automatically;" >&2; \
	  echo "  otherwise install the pinned band and rerun make test, e.g.:" >&2; \
	  echo "    dotnet-install.sh --channel 8.0 --install-dir \"\$$HOME/.cache/dotnet-sdk\"" >&2; exit 127; fi
	@if ! dotnet --list-sdks 2>/dev/null | grep -q .; then \
	  echo "ERROR: make test needs the .NET SDK pinned by global.json (8.0 band); the dotnet on PATH resolved no installed SDKs (runtime-only host?)." >&2; \
	  echo "  Install the pinned band, e.g.: dotnet-install.sh --channel 8.0 --install-dir \"\$$HOME/.cache/dotnet-sdk\"" >&2; \
	  echo "  (auto-detected by this Makefile), or your distro's dotnet-sdk-8.0 package, and rerun make test." >&2; exit 127; fi
# -x follows sourced files so checks see through `. ./lib.sh` style sharing.
	shellcheck -x $(wildcard $(ROOT)/scripts/*.sh)
	ruff check $(ROOT)/scripts
	mypy $(ROOT)/scripts
# Stdlib-only syntax gate for the scripts make test never executes
# (validate_*.py / measure_es_onoff.py need a live server). Bytecode lands in
# scripts/__pycache__, which is gitignored.
	python3 -m compileall -q $(ROOT)/scripts
# Locked restore: fails when a PackageReference changed without regenerating
# packages.lock.json, instead of silently floating to newer versions.
	dotnet restore --locked-mode $(ROOT)/Source/EfficientServer.Tests
	dotnet run --project $(ROOT)/Source/EfficientServer.Tests -c Release --no-restore
	python3 $(ROOT)/scripts/check_config_doc.py
	python3 $(ROOT)/scripts/check_config_doc.py --selftest
	python3 $(ROOT)/scripts/check_version.py
	python3 $(ROOT)/scripts/check_version.py --selftest
	python3 $(ROOT)/scripts/es_cfg_guard.py --selftest
	python3 $(ROOT)/scripts/gen_sbom.py --selftest
	python3 $(ROOT)/scripts/coverage_badge.py --selftest

# Line coverage of the unit suite via dotnet-coverage. Writes
# TestResults/coverage.cobertura.xml; CI renders it into the README badge
# with scripts/coverage_badge.py.
#
# The tool lives in .config/dotnet-tools.json (local manifest): such tools get
# no PATH shim, so invoke as `dotnet dotnet-coverage ...` and let the host CLI
# resolve them. Output format flag is 18.x spelling (-f/--output-format).
coverage:
	dotnet tool restore
	mkdir -p "$(ROOT)/TestResults"
	# Same locked restore make test runs: the collect below passes --no-restore.
	dotnet restore --locked-mode $(ROOT)/Source/EfficientServer.Tests
	dotnet tool run dotnet-coverage -- collect -f cobertura -o "$(ROOT)/TestResults/coverage.cobertura.xml" -- dotnet run --project "$(ROOT)/Source/EfficientServer.Tests" -c Release --no-restore
install:
	$(ROOT)/scripts/install.sh
# $(SEVENDTD_DS_DIR), not $(DS): an exported SEVENDTD_DS_DIR overrides ?=, so
# $(DS) would still hold the stock default and this could delete from a
# directory install.sh never touched. The variable equals DS when DS was used.
uninstall:
	rm -rf "$(SEVENDTD_DS_DIR)/Mods/EfficientServer"
run:
	$(ROOT)/scripts/run_server.sh
clean:
	rm -rf $(ROOT)/dist $(ROOT)/Source/EfficientServer/bin $(ROOT)/Source/EfficientServer/obj \
	       $(ROOT)/Source/EfficientServer.Tests/bin $(ROOT)/Source/EfficientServer.Tests/obj
