#!/usr/bin/env bash
# Build + package the CHORUS Windows SysTray voice client.
# Produces a self-contained win-x64 EXE (no .NET runtime needed on the target).
#
# NOTE: Concentus loads its native codec via raw LoadLibrary("opus.dll") on
# Windows, which does NOT follow the runtimes/<rid>/native/ layout. The
# win-x64 opus.dll must sit NEXT TO the EXE — that is why we copy it here.
set -euo pipefail
cd "$(dirname "$0")"

CONFIG="${1:-Release}"
RID="${2:-win-x64}"

dotnet build Chorus.sln -c "$CONFIG"

echo
echo ">>> Publishing self-contained ${RID} (one deployable EXE) ..."
dotnet publish src/Chorus.App/Chorus.App.csproj \
  -c "$CONFIG" -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "dist/${RID}"

echo
echo ">>> Copying native opus.dll next to the EXE (raw LoadLibrary path) ..."
OPUS_SRC="src/Chorus.App/bin/${CONFIG}/net9.0-windows/${RID}/runtimes/${RID}/native/opus.dll"
if [ -f "$OPUS_SRC" ]; then
  cp "$OPUS_SRC" "dist/${RID}/opus.dll"
  echo "    dist/${RID}/opus.dll"
else
  echo "    WARNING: opus.dll not found at $OPUS_SRC — audio will be silent"
fi

echo
echo ">>> Done. Deployable: client/dist/${RID}/Chorus.exe + opus.dll"
echo "    (copy both files to Windows and run Chorus.exe — no .NET install needed)"
