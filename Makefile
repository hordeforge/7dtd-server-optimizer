ROOT := $(CURDIR)
DS ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server
# Scripts read SEVENDTD_DS_DIR; route the documented `make install DS=...`
# variable through so both spellings work and cannot drift apart.
export SEVENDTD_DS_DIR ?= $(DS)

# Prefer a local SDK if present (cache or ~/.dotnet), like 7dtd-loadgen
DOTNET_ROOT ?= $(firstword \
	$(wildcard $(HOME)/.cache/dotnet-sdk) \
	$(wildcard $(HOME)/.dotnet) \
	)
ifneq ($(DOTNET_ROOT),)
  export DOTNET_ROOT
  export PATH := $(DOTNET_ROOT):$(PATH)
endif

# Bare `make` must orient a fresh contributor, not fail on a missing game
# install (the old implicit default target, build, did exactly that).
.DEFAULT_GOAL := help

.PHONY: help build build-mcs test install uninstall run clean package verify-reproducible
help:
	@echo "EfficientServer: Harmony optimization mod for 7 Days to Die dedicated servers"
	@echo
	@echo "Contributor loop (works without a game install):"
	@echo "  make test              Every CI gate: shellcheck + script syntax +"
	@echo "                         config harness + doc/version consistency gates"
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
# Runs after PATH setup above, so a local SDK under ~/.cache/dotnet-sdk or
# ~/.dotnet counts as found.
test:
	@if ! command -v shellcheck >/dev/null 2>&1; then \
	  echo "ERROR: make test needs shellcheck (lint gate for scripts/*.sh)." >&2; \
	  echo "  Install it (e.g. apt-get install shellcheck) and rerun make test." >&2; exit 127; fi
	@if ! command -v python3 >/dev/null 2>&1; then \
	  echo "ERROR: make test needs python3 (config-doc, version and cfg-guard gates)." >&2; \
	  echo "  Install python3 and rerun make test." >&2; exit 127; fi
	@if ! command -v dotnet >/dev/null 2>&1; then \
	  echo "ERROR: make test needs the .NET SDK pinned by global.json (8.0 band), but dotnet is not on PATH." >&2; \
	  echo "  A local SDK in ~/.cache/dotnet-sdk or ~/.dotnet is picked up automatically; otherwise install the SDK and rerun make test." >&2; exit 127; fi
	shellcheck $(wildcard $(ROOT)/scripts/*.sh)
# Stdlib-only syntax gate for the scripts make test never executes
# (validate_*.py / measure_es_onoff.py need a live server). Bytecode lands in
# scripts/__pycache__, which is gitignored.
	python3 -m compileall -q $(ROOT)/scripts
# Locked restore: fails when a PackageReference changed without regenerating
# packages.lock.json, instead of silently floating to newer versions.
	dotnet restore --locked-mode $(ROOT)/Source/EfficientServer.Tests
	dotnet run --project $(ROOT)/Source/EfficientServer.Tests -c Release --no-restore
	python3 $(ROOT)/scripts/check_config_doc.py
	python3 $(ROOT)/scripts/check_version.py
	python3 $(ROOT)/scripts/es_cfg_guard.py --selftest
	python3 $(ROOT)/scripts/gen_sbom.py --selftest
install:
	$(ROOT)/scripts/install.sh
uninstall:
	rm -rf "$(DS)/Mods/EfficientServer"
run:
	$(ROOT)/scripts/run_server.sh
clean:
	rm -rf $(ROOT)/dist $(ROOT)/Source/EfficientServer/bin $(ROOT)/Source/EfficientServer/obj \
	       $(ROOT)/Source/EfficientServer.Tests/bin $(ROOT)/Source/EfficientServer.Tests/obj
