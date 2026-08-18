#!/usr/bin/env bash
# Builds release-ready, self-contained Moonlace binaries and packs them into
# dist/. No .NET runtime is needed on the target machine.
#
#   scripts/build-release.sh            # linux-x64 + win-x64
#   scripts/build-release.sh linux-x64  # one runtime only

set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(grep -oPm1 '(?<=<Version>)[^<]+' src/Moonlace.App/Moonlace.App.csproj)
RIDS=("${@:-linux-x64}")
if [ $# -eq 0 ]; then
    RIDS=(linux-x64 win-x64)
fi

rm -rf dist
mkdir -p dist

for RID in "${RIDS[@]}"; do
    echo "==> Publishing $RID"
    OUT="dist/publish-$RID"
    dotnet publish src/Moonlace.App -c Release -r "$RID" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=none \
        -o "$OUT"

    STAGE="dist/Moonlace-$VERSION-$RID"
    mkdir -p "$STAGE"
    cp -r "$OUT"/. "$STAGE/"
    cp README.md "$STAGE/"

    # Debug symbols some native packages (SkiaSharp/HarfBuzz) ship — not
    # needed to run, only for crash-dump debugging, and huge.
    find "$STAGE" -name '*.pdb' -delete

    echo "==> Packing $STAGE"
    if [[ "$RID" == win-* ]]; then
        (cd dist && zip -qr "Moonlace-$VERSION-$RID.zip" "Moonlace-$VERSION-$RID")
    else
        tar -czf "dist/Moonlace-$VERSION-$RID.tar.gz" -C dist "Moonlace-$VERSION-$RID"
    fi
    rm -rf "$OUT" "$STAGE"
done

echo
echo "Release artifacts:"
ls -lh dist
