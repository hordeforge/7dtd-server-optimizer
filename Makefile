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

.PHONY: build build-mcs test install uninstall run clean package
build:
	$(ROOT)/scripts/build.sh
package:
	$(ROOT)/scripts/package.sh
build-mcs:
	SEVENDTD_BUILD_BACKEND=mcs $(ROOT)/scripts/build.sh
test:
	shellcheck $(wildcard $(ROOT)/scripts/*.sh)
	dotnet run --project $(ROOT)/Source/EfficientServer.Tests -c Release
	python3 $(ROOT)/scripts/check_config_doc.py
	python3 $(ROOT)/scripts/check_version.py
install:
	$(ROOT)/scripts/install.sh
uninstall:
	rm -rf "$(DS)/Mods/EfficientServer"
run:
	$(ROOT)/scripts/run_server.sh
clean:
	rm -rf $(ROOT)/dist $(ROOT)/Source/EfficientServer/bin $(ROOT)/Source/EfficientServer/obj \
	       $(ROOT)/Source/EfficientServer.Tests/bin $(ROOT)/Source/EfficientServer.Tests/obj
