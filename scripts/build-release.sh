#!/usr/bin/env bash
# Builds Velopack release packages for Moonlace and (optionally) uploads them
# to a GitHub release. Installed copies auto-update from GitHub releases; the
# Windows portable zip and the Linux AppImage self-update in place too.
#
#   scripts/build-release.sh                 # pack linux-x64 + win-x64 into dist/releases
#   scripts/build-release.sh linux-x64       # one runtime only
#   scripts/build-release.sh --upload        # pack both + publish the GitHub release
#                                            # (needs GITHUB_TOKEN with repo scope)
#
# Requires the Velopack CLI: dotnet tool install -g vpk

set -euo pipefail
cd "$(dirname "$0")/.."

REPO_URL="https://github.com/RayaSerahill/Moonlace"
VERSION=$(grep -oPm1 '(?<=<Version>)[^<]+' src/Moonlace.App/Moonlace.App.csproj)

UPLOAD=0
RIDS=()
for ARG in "$@"; do
    case "$ARG" in
        --upload) UPLOAD=1 ;;
        *) RIDS+=("$ARG") ;;
    esac
done
if [ ${#RIDS[@]} -eq 0 ]; then
    RIDS=(linux-x64 win-x64)
fi

if ! command -v vpk >/dev/null; then
    echo "vpk not found; install it with: dotnet tool install -g vpk" >&2
    exit 1
fi
if [ "$UPLOAD" -eq 1 ] && [ -z "${GITHUB_TOKEN:-}" ]; then
    echo "--upload needs GITHUB_TOKEN set" >&2
    exit 1
fi

RELEASES="dist/releases"
rm -rf dist
mkdir -p "$RELEASES"

for RID in "${RIDS[@]}"; do
    case "$RID" in
        win-*)   VPK_OS=win;   CHANNEL=win;   MAIN_EXE=Moonlace.exe ;;
        linux-*) VPK_OS=linux; CHANNEL=linux; MAIN_EXE=Moonlace ;;
        *) echo "Unsupported runtime: $RID" >&2; exit 1 ;;
    esac

    echo "==> Publishing $RID"
    OUT="dist/publish-$RID"
    # No single-file bundling here: Velopack packages the publish directory
    # itself and does not support PublishSingleFile executables.
    dotnet publish src/Moonlace.App -c Release -r "$RID" \
        --self-contained true \
        -p:DebugType=none \
        -o "$OUT"
    cp README.md "$OUT/"

    # Previous release enables delta package generation. Fine to fail on the
    # very first release or offline; full packages still work.
    vpk download github --repoUrl "$REPO_URL" -c "$CHANNEL" -o "$RELEASES" \
        || echo "==> No previous $CHANNEL release found; skipping deltas"

    echo "==> Packing $RID ($CHANNEL channel)"
    vpk "[$VPK_OS]" pack \
        --packId Moonlace \
        --packVersion "$VERSION" \
        --packDir "$OUT" \
        --mainExe "$MAIN_EXE" \
        --packTitle Moonlace \
        --packAuthors "Raya Serahill" \
        -c "$CHANNEL" \
        -o "$RELEASES"

    if [ "$UPLOAD" -eq 1 ]; then
        echo "==> Uploading $CHANNEL packages to GitHub"
        # --merge lets the second channel land in the same vX.Y.Z release.
        vpk upload github \
            --repoUrl "$REPO_URL" \
            --token "$GITHUB_TOKEN" \
            -c "$CHANNEL" \
            -o "$RELEASES" \
            --tag "v$VERSION" \
            --releaseName "Moonlace v$VERSION" \
            --merge \
            --publish
    fi

    rm -rf "$OUT"
done

echo
echo "Release artifacts:"
ls -lh "$RELEASES"
