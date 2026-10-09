#!/usr/bin/env bash
# Build train/native/squad.so for the cluster in the Linux toolchain image (train/cluster/Dockerfile.build).
# The sources are copied inside the container, so Windows bin/obj folders do not leak into the Linux build.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
docker image inspect squad-build > /dev/null 2>&1 || docker build -t squad-build -f "$root/train/cluster/Dockerfile.build" "$root/train/cluster"
MSYS_NO_PATHCONV=1 docker run --rm -v "$root:/src" -v squad-nuget:/root/.nuget squad-build bash -c '
  set -e
  mkdir -p /b/sim && cp /src/sim/Directory.Build.props /b/sim/
  cd /src && tar --exclude=bin --exclude=obj -cf - sim/Squad.Sim sim/Squad.Bots sim/Squad.Train | tar -xf - -C /b
  dotnet publish /b/sim/Squad.Train -r linux-x64 -c Release -p:PublishAot=true -p:NativeLib=Shared -o /b/native -nologo
  mkdir -p /src/train/native && cp /b/native/squad.so /src/train/native/squad.so
  ls -l /src/train/native/squad.so
  echo "newest glibc symbol: $(objdump -T /src/train/native/squad.so | grep -o "GLIBC_[0-9.]*" | sort -Vu | tail -1)"'
