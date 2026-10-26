#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
configuration="${CONFIGURATION:-Release}"
sdk_root="${DOLPHIN_DOTNET_ROOT:-$PWD/vendor/sdk}"
if [[ ! -d "$sdk_root" && -d "$PWD/../DolphinDotNet" ]]; then sdk_root="$PWD/../DolphinDotNet"; fi
sdk_root="$(cd "$sdk_root" && pwd)"
mkdir -p out
python3 tools/build_assets.py --resolution "${FIXTURE_RESOLUTION:-48}"
python3 -m unittest discover -s tests -p 'test_*.py'
dotnet run --project tests/Hypercube.Tests -c "$configuration" -- out/assets/world.hcp
dotnet build "$sdk_root/tools/DolphinDotNet.Compiler" -c Release -m:1
dotnet build samples/Validation/Validation.csproj -c "$configuration" -p:DndBuildNative=false -p:HypercubeCI=true -p:DolphinDotNetRoot="$sdk_root" -m:1
dotnet "$sdk_root/tools/DolphinDotNet.Compiler/bin/Release/net8.0/DolphinDotNet.Compiler.dll" --bundle "samples/Validation/bin/$configuration/net8.0/Validation.dll" "$PWD/out/native-$configuration"
# Native toolchain is pinned; allow a pre-cloned libogc2 checkout for restricted networks.
native_args=()
if [[ -n "${LIBOGC2_SOURCE:-}" ]]; then
  native_args+=(--mount "type=bind,src=$LIBOGC2_SOURCE,dst=/upstream" -e DND_EXI_SOURCE_DIR=/upstream)
fi
if [[ -n "${CA_CERT_FILE:-}" ]]; then
  native_args+=(--mount "type=bind,src=$CA_CERT_FILE,dst=/tmp/dnd-ca.pem,readonly" -e GIT_SSL_CAINFO=/tmp/dnd-ca.pem -e SSL_CERT_FILE=/tmp/dnd-ca.pem)
fi
docker run --rm --init --user "$(id -u):$(id -g)" --mount "type=bind,src=$PWD/out/native-$configuration,dst=/work" "${native_args[@]}" -w /work ghcr.io/extremscorner/libogc2@sha256:13bb658d3f18903816617223f5d7c6f776d3db3ea806aae67f8d35a7a69d4c85 make -j2
