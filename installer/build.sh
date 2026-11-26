#!/usr/bin/env bash
# Builds publish/UCut (self-contained, win-x64) and publish/UCut-Setup-<version>.exe.
# Works on Linux (apt install nsis) and in Git Bash on Windows (NSIS in PATH).
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
OUT=publish/UCut
rm -rf "$OUT"
common=(-c Release -r win-x64 --self-contained -p:DebugType=none -o "$OUT")
dotnet publish src/UCut.Desktop "${common[@]}"
dotnet publish src/UCut.Cli "${common[@]}"
# Whisper ships native libraries for every platform; only win-x64 is needed.
find "$OUT/runtimes" -mindepth 1 -maxdepth 1 ! -name win-x64 -exec rm -rf {} +
rm -f "$OUT/ggml-metal.metal"
makensis -V2 -DVERSION="$VERSION" -DSOURCE="../$OUT" -DOUTFILE="../publish/UCut-Setup-$VERSION.exe" installer/UCut.nsi
ls -la publish
