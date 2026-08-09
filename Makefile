ROOT := $(CURDIR)
DS ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server

.PHONY: build build-mcs test install uninstall run clean
build:
	$(ROOT)/scripts/build.sh
build-mcs:
	SEVENDTD_BUILD_BACKEND=mcs $(ROOT)/scripts/build.sh
test:
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
	rm -rf $(ROOT)/dist $(ROOT)/Source/EfficientServer/bin $(ROOT)/Source/EfficientServer/obj
