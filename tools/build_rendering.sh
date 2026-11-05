#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
configuration="${CONFIGURATION:-Release}"
sdk_root="${DOLPHIN_DOTNET_ROOT:-$PWD/vendor/sdk}"
sdk_root="$(cd "$sdk_root" && pwd)"
native_output="$PWD/out/native-rendering-$configuration"
dotnet build "$sdk_root/tools/DolphinDotNet.Compiler" -c Release -m:1
dotnet build samples/Rendering/Rendering.csproj -c "$configuration" -p:DolphinDotNetRoot="$sdk_root" -p:DndBuildNative=false -m:1
dotnet "$sdk_root/tools/DolphinDotNet.Compiler/bin/Release/net8.0/DolphinDotNet.Compiler.dll" --bundle "samples/Rendering/bin/$configuration/net8.0/Rendering.dll" "$native_output"
native_args=()
if [[ -n "${LIBOGC2_SOURCE:-}" ]]; then native_args+=(--mount "type=bind,src=$LIBOGC2_SOURCE,dst=/upstream" -e DND_EXI_SOURCE_DIR=/upstream); fi
if [[ -n "${CA_CERT_FILE:-}" ]]; then native_args+=(--mount "type=bind,src=$CA_CERT_FILE,dst=/tmp/dnd-ca.pem,readonly" -e GIT_SSL_CAINFO=/tmp/dnd-ca.pem -e SSL_CERT_FILE=/tmp/dnd-ca.pem); fi
docker run --rm --init --user "$(id -u):$(id -g)" --mount "type=bind,src=$native_output,dst=/work" "${native_args[@]}" -w /work ghcr.io/extremscorner/libogc2@sha256:13bb658d3f18903816617223f5d7c6f776d3db3ea806aae67f8d35a7a69d4c85 make -j2
