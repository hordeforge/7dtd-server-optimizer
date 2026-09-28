ROOT := $(CURDIR)
# The stock /tmp is tmpfs on most Linux hosts, so temp files there are held in
# RAM and vanish on reboot. Route every gate's scratch onto disk: mktemp,
# Python's tempfile and .NET's Path.GetTempPath all honor TMPDIR, so this one
# export covers the shell, Python and C# sides. .scratch/ is gitignored.
export TMPDIR := $(ROOT)/.scratch/tmp
# Create it while the makefile is read, not in a recipe: make resolves the
# exported TMPDIR before any target runs, so on a fresh clone it printed
# "TMPDIR value ...: No such file or directory / using default temporary
# directory '/tmp'" on every invocation and used the stock tmpfs for its own
# temporaries, which is the thing the export above exists to avoid.
$(shell mkdir -p "$(TMPDIR)")

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

# Exact ruff pin. make test refuses other versions so lint rule behavior
# cannot silently diverge between a green local run and a red remote one (or
# vice versa). .github/workflows/ci.yml installs it from `make ruff-version`
# rather than repeating the number, so the gate cannot drift from this line.
# Bump = reviewed change of this line plus README.md, like global.json's pin.
RUFF_VERSION := 0.16.4

# Exact mypy pin. Same rationale as RUFF_VERSION: checker behavior diverges
# between versions, so the type gate must be identical on both sides.
MYPY_VERSION := 2.1.0

.PHONY: help build build-mcs test lint unit unit-list check-scripts preflight-lint preflight-unit \
	preflight-scripts scratch coverage install uninstall run clean package verify-reproducible \
	backup-config ruff-version mypy-version

# Read by .github/workflows/ci.yml (`make -s ruff-version`) so the pinned
# version has one source of truth. Printing it here beats a second literal in
# ci.yml, which silently installs a different linter than the one make test
# demands and fails with a version error nobody reads.
ruff-version:
	@echo "$(RUFF_VERSION)"
mypy-version:
	@echo "$(MYPY_VERSION)"

# Every gate that can reach Python's tempfile, .NET's Path.GetTempPath or
# mktemp depends on this. A nonexistent TMPDIR is silently ignored by each of
# them, and Python's tempfile.gettempdir() then falls back to the stock /tmp,
# undoing the routing above. The subset targets ran without it, so `make unit`
# and `make check-scripts` alone wrote their scratch into RAM. The parse-time
# mkdir near the TMPDIR export already covers the directory; this stays the
# declared prereq, so a removed .scratch/ cannot silently unroute a gate.
scratch:
	@mkdir -p "$(TMPDIR)"
# Reap the .NET SDK's own leftovers, which it never removes: every
# `dotnet build` creates an empty MSBuildTemp*/NuGetScratch* (plus a
# randomly named) directory under TMPDIR and leaves it behind, so the
# scratch root gains a few empty dirs per build and nothing else ever
# removes them. Empty AND older than a day, so an in-flight build's temp
# (which lives for seconds) can never be caught, and the sweep is
# restricted to those SDK shapes: a script that deliberately KEEPS a
# directory there (install.sh's preserved Config backup on a failed
# install) is never a candidate. Scratch dirs, not files: a leftover file
# with bytes in it is evidence of a killed run, not ours to delete.
	@find "$(TMPDIR)" -mindepth 1 -maxdepth 1 -type d -empty -mtime +1 \
	  \( -name 'MSBuildTemp*' -o -name 'NuGetScratch*' \
	     -o -regextype posix-extended \
	        -regex '.*/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}' \) \
	  -exec rm -rf {} + 2>/dev/null || true
help:
	@echo "EfficientServer: Harmony optimization mod for 7 Days to Die dedicated servers"
	@echo
	@echo "Contributor loop (works without a game install):"
	@echo "  make test              Every CI gate, same as CI (what a PR must pass)"
	@echo
	@echo "  Subset targets for the edit-test loop; each needs only its own tools,"
	@echo "  and 'make test' is exactly these three in sequence:"
	@echo "  make lint              shellcheck + ruff + mypy on scripts/"
	@echo "  make unit              Config.Load/Normalize harness (Source/EfficientServer.Tests)"
	@echo "  make check-scripts     Python gates: config doc/version, repo_root,"
	@echo "                         cfg guard and coverage badge (needs python3 only)"
	@echo
	@echo "  Single check in the unit harness (quote a '*' so the shell keeps it):"
	@echo "  make unit FILTER='Governor*'   Only matching checks; no match exits 1"
	@echo "  make unit-list                  List the check names a FILTER can match"
	@echo
	@echo "  make clean             Remove dist/, TestResults/ and bin/obj build outputs"
	@echo "  make coverage          Run the unit suite under dotnet-coverage into"
	@echo "                         TestResults/coverage.cobertura.xml"
	@echo
	@echo "Game-backed targets (need DS=/path/to/'7 Days to Die Dedicated Server',"
	@echo "default: ~/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server):"
	@echo "  make build             Compile dist/EfficientServer against game DLLs"
	@echo "  make build-mcs         Same, forcing the Mono mcs fallback backend"
	@echo "  make install           Build and copy into \$$DS/Mods/EfficientServer"
	@echo "  make uninstall         Remove \$$DS/Mods/EfficientServer, keeping the"
	@echo "                         live config under \$$DS/EfficientServer-uninstall-backup"
	@echo "  make backup-config ES_CONFIG_BACKUP_DEST=/mnt/backup/es-config"
	@echo "                         Copy the live config off the install tree and"
	@echo "                         verify the copy (--verify re-checks it later)"
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
# The gate is split three ways so an edit only pays for the tools its file
# family needs, and so a contributor touching one area is not told to install
# all four. `make test` is exactly these three targets, in this order, and is
# what .github/workflows/ci.yml runs: keep the two lists identical.
#
# Preflights are per target rather than one block in `test`, so `make unit` on
# a machine with no shellcheck says nothing about shellcheck, and a missing
# tool is named by the target that actually needs it. Each preflight runs
# after the PATH setup above, so a real SDK under ~/.cache/dotnet-sdk or
# ~/.dotnet counts as found.

# Named errors on a clean machine, instead of a bare "No such file or
# directory" (Error 127) or mid-gate resolver noise from whichever gate
# happens to run first.
preflight-lint:
	@if ! command -v shellcheck >/dev/null 2>&1; then \
	  echo "ERROR: make lint needs shellcheck (lint gate for scripts/*.sh)." >&2; \
	  echo "  Install it (e.g. apt-get install shellcheck) and rerun make lint." >&2; exit 127; fi
	@if ! command -v ruff >/dev/null 2>&1; then \
	  echo "ERROR: make lint needs ruff $(RUFF_VERSION) (lint gate for scripts/*.py, config in ruff.toml)." >&2; \
	  echo "  Install the pinned version: uv tool install ruff==$(RUFF_VERSION) and rerun make lint." >&2; exit 127; fi
	@if [ "$$(ruff --version 2>/dev/null | awk '{print $$2}')" != "$(RUFF_VERSION)" ]; then \
	  echo "ERROR: make lint needs ruff exactly $(RUFF_VERSION), matching .github/workflows/ci.yml; found $$(ruff --version 2>/dev/null)." >&2; \
	  echo "  Rule behavior diverges between versions, so CI and local runs must agree:" >&2; \
	  echo "  uv tool install --force ruff==$(RUFF_VERSION)." >&2; exit 1; fi
	@if ! command -v mypy >/dev/null 2>&1; then \
	  echo "ERROR: make lint needs mypy $(MYPY_VERSION) (type gate for scripts/*.py, config in mypy.ini)." >&2; \
	  echo "  Install the pinned version: uv tool install mypy==$(MYPY_VERSION) and rerun make lint." >&2; exit 127; fi
	@if [ "$$(mypy --version 2>/dev/null | awk '{print $$2}')" != "$(MYPY_VERSION)" ]; then \
	  echo "ERROR: make lint needs mypy exactly $(MYPY_VERSION), matching .github/workflows/ci.yml; found $$(mypy --version 2>/dev/null)." >&2; \
	  echo "  Checker behavior diverges between versions, so CI and local runs must agree:" >&2; \
	  echo "  uv tool install --force mypy==$(MYPY_VERSION)." >&2; exit 1; fi

preflight-scripts:
	@if ! command -v python3 >/dev/null 2>&1; then \
	  echo "ERROR: make check-scripts needs python3 (config-doc, version and cfg-guard gates)." >&2; \
	  echo "  Install python3 and rerun make check-scripts." >&2; exit 127; fi

# The second dotnet gate catches runtime-only hosts (distro 'dotnet' with zero
# SDKs) that would otherwise sail past `command -v` and die mid-gate inside
# `dotnet restore` with resolver noise.
preflight-unit:
	@if ! command -v dotnet >/dev/null 2>&1; then \
	  echo "ERROR: make unit needs the .NET SDK pinned by global.json (8.0 band), but no dotnet is on PATH." >&2; \
	  echo "  A real SDK install under ~/.cache/dotnet-sdk or ~/.dotnet is picked up automatically;" >&2; \
	  echo "  otherwise install the pinned band and rerun make unit, e.g.:" >&2; \
	  echo "    dotnet-install.sh --channel 8.0 --install-dir \"\$$HOME/.cache/dotnet-sdk\"" >&2; exit 127; fi
	@if ! dotnet --list-sdks 2>/dev/null | grep -q .; then \
	  echo "ERROR: make unit needs the .NET SDK pinned by global.json (8.0 band); the dotnet on PATH resolved no installed SDKs (runtime-only host?)." >&2; \
	  echo "  Install the pinned band, e.g.: dotnet-install.sh --channel 8.0 --install-dir \"\$$HOME/.cache/dotnet-sdk\"" >&2; \
	  echo "  (auto-detected by this Makefile), or your distro's dotnet-sdk-8.0 package, and rerun make unit." >&2; exit 127; fi

# -x follows sourced files so checks see through `. ./lib.sh` style sharing.
lint: preflight-lint scratch
	shellcheck -x $(wildcard $(ROOT)/scripts/*.sh)
	ruff check $(ROOT)/scripts
	mypy $(ROOT)/scripts

# Locked restore: fails when a PackageReference changed without regenerating
# packages.lock.json, instead of silently floating to newer versions.
#
# FILTER selects one check while debugging: `make unit FILTER=Governor*`
# (quote it if the shell would glob) runs only the checks whose description
# matches. A filter that matches nothing exits 1 rather than reporting a clean
# run. `make test` and CI never set it, so the gate still runs everything.
UNIT_ARGS := $(if $(FILTER),-- --filter "$(FILTER)",)
unit: preflight-unit scratch
# Both projects restore locked. The test project is the suite; the mod project
# is here because nothing else in this gate touches it, and its committed
# packages.lock.json would otherwise never be read: `make build` needs a game
# install, which CI does not have, so a PackageReference bumped without the
# lock file regenerated went unnoticed until a maintainer's own build failed.
# Restore resolves packages only; the game assemblies are bound as build-time
# references (HintPath), so no game install is needed to check the hash.
	dotnet restore --locked-mode $(ROOT)/Source/EfficientServer
	dotnet restore --locked-mode $(ROOT)/Source/EfficientServer.Tests
	dotnet run --project $(ROOT)/Source/EfficientServer.Tests -c Release --no-restore $(UNIT_ARGS)

# Check names, one per line, for picking a FILTER. Runs the suite to reach the
# checks, so it costs a unit run, but it asserts nothing and always exits 0.
# The recipe is silenced (unlike the other targets) because the whole point is
# a pipeable name list: `make unit-list | grep Governor`.
unit-list: preflight-unit scratch
	@dotnet restore --locked-mode $(ROOT)/Source/EfficientServer.Tests >/dev/null
	@dotnet run --project $(ROOT)/Source/EfficientServer.Tests -c Release --no-restore -- --list $(if $(FILTER),--filter "$(FILTER)",)

check-scripts: preflight-scripts scratch
# Stdlib-only syntax gate for the scripts these targets never execute
# (validate_*.py / measure_es_onoff.py need a live server). Bytecode lands in
# scripts/__pycache__, which is gitignored.
	python3 -m compileall -q $(ROOT)/scripts
	python3 $(ROOT)/scripts/repo_root.py --selftest
	python3 $(ROOT)/scripts/check_config_doc.py
	python3 $(ROOT)/scripts/check_config_doc.py --selftest
	python3 $(ROOT)/scripts/check_version.py
	python3 $(ROOT)/scripts/check_version.py --selftest
	python3 $(ROOT)/scripts/es_cfg_guard.py --selftest
	python3 $(ROOT)/scripts/bench_parse.py --selftest
	python3 $(ROOT)/scripts/coverage_badge.py --selftest
	python3 $(ROOT)/scripts/backup_config.py --selftest

test:
# Order matters and is the CI order: shell lints, then the .NET harness, then
# the doc/version gates. A preflight failure in any of them stops the run
# before the next tool is needed.
	$(MAKE) --no-print-directory lint
	$(MAKE) --no-print-directory unit
	$(MAKE) --no-print-directory check-scripts

# Line coverage of the unit suite via dotnet-coverage. Writes
# TestResults/coverage.cobertura.xml; CI renders it into the README badge
# with scripts/coverage_badge.py.
#
# The tool lives in .config/dotnet-tools.json (local manifest): such tools get
# no PATH shim, so invoke as `dotnet dotnet-coverage ...` and let the host CLI
# resolve them. Output format flag is 18.x spelling (-f/--output-format).
coverage: scratch
	dotnet tool restore
	mkdir -p "$(ROOT)/TestResults"
	# Same locked restore make test runs: the collect below passes --no-restore.
	dotnet restore --locked-mode $(ROOT)/Source/EfficientServer.Tests
	dotnet tool run dotnet-coverage -- collect -f cobertura -o "$(ROOT)/TestResults/coverage.cobertura.xml" -- dotnet run --project "$(ROOT)/Source/EfficientServer.Tests" -c Release --no-restore
install:
	$(ROOT)/scripts/install.sh
# The install dir guard and the config-preserving copy live in uninstall.sh,
# next to install.sh's own guard, so both halves of the same rm -rf are in one
# place. SEVENDTD_UNINSTALL_PURGE=1 opts into deleting the config as well.
uninstall:
	$(ROOT)/scripts/uninstall.sh
# Off-host snapshot of the live config, the one piece of state on the host that
# nothing regenerates. The destination must be OFF the install tree: a copy on
# the same disk is lost by the same disaster as the config it protects, and
# backup_config.py refuses it. Verify on a schedule with
#   scripts/backup_config.py --dest "$(ES_CONFIG_BACKUP_DEST)" --verify
backup-config:
	@test -n "$(ES_CONFIG_BACKUP_DEST)" || { \
	  echo "ERROR: set ES_CONFIG_BACKUP_DEST to a directory OFF the server install" >&2; \
	  echo "  e.g. ES_CONFIG_BACKUP_DEST=/mnt/backup/es-config DS=/srv/7dtd make backup-config" >&2; \
	  exit 2; }
	$(ROOT)/scripts/backup_config.py --dest "$(ES_CONFIG_BACKUP_DEST)"
run:
	$(ROOT)/scripts/run_server.sh
# TestResults/ holds the coverage XML the badge job reads; leaving it behind
# means a rerun of coverage_badge.py after a failed `make coverage` can publish
# a stale report as if it were current.
clean:
	rm -rf $(ROOT)/dist $(ROOT)/TestResults \
	       $(ROOT)/Source/EfficientServer/bin $(ROOT)/Source/EfficientServer/obj \
	       $(ROOT)/Source/EfficientServer.Tests/bin $(ROOT)/Source/EfficientServer.Tests/obj
