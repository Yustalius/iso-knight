#!/usr/bin/env bash
# Build libsquad (the simulation, the rule bots and the training environment) as a native shared library for Python.
# Needs the .NET 8 SDK and clang (Native AOT). Build on the machine that trains, or on a Linux no newer than it
# (the library links against the build machine's glibc).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
rid="${RID:-linux-x64}"
dotnet publish "$here/../sim/Squad.Train" -r "$rid" -c Release -p:PublishAot=true -p:NativeLib=Shared -o "$here/native" -nologo
ls -l "$here/native/squad."*
