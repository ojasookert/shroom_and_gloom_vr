#!/usr/bin/env bash
# Build a release into dist/: the mod package, the installer AppImage and manifest.json.
# Usage: tools/package.sh <version> [url the release files will be downloaded from]
set -euo pipefail
version=${1:?usage: tools/package.sh <version> [download url]}
version=${version#v}
repo_dir=$(cd "$(dirname "$0")/.." && pwd)
url_base=${2:-https://github.com/ojasookert/shroom_and_gloom_vr/releases/download/v$version}
dist=$repo_dir/dist
dotnet=${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}

bepinex_version=6.0.0-be.755
bepinex_url='https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip'
bepinex_sha256=3616d6a67f5f595973ec4aa7bd7edaf7f799d5bb9926f7146a6dcc7b4abf478f
appimagetool_url=https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
appimagetool_sha256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
runtime_url=https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64
runtime_sha256=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d

fetch() { # url sha256 dest
    [ -f "$3" ] && echo "$2  $3" | sha256sum -c --quiet 2>/dev/null && return
    curl -fsSL -o "$3" "$1"
    echo "$2  $3" | sha256sum -c --quiet
}

rm -rf "$dist"
mkdir -p "$dist"

# The mod, laid out like the game folder.
"$dotnet" build "$repo_dir/src/ShroomVR/ShroomVR.csproj" -c Release -nologo -v q -p:Version="$version"
package=$dist/package
plugin=$package/BepInEx/plugins/ShroomVR
mkdir -p "$plugin"
cp "$repo_dir/src/ShroomVR/bin/Release/net6.0/ShroomVR.dll" "$plugin/"
cp -r "$repo_dir/licenses" "$plugin/licenses"
cp "$repo_dir/LICENSE" "$plugin/licenses/ShroomVR-LICENSE.txt"
cp -r "$repo_dir/game/." "$package/"
zip_name=ShroomVR-$version.zip
(cd "$package" && find . -type f | sort | zip -qX "$dist/$zip_name" -@)

# The installer.
SHROOMVR_VERSION=$version cargo build --release --locked --manifest-path "$repo_dir/installer/Cargo.toml"
cache=${XDG_CACHE_HOME:-$HOME/.cache}/shroomvr-build
mkdir -p "$cache"
fetch "$appimagetool_url" "$appimagetool_sha256" "$cache/appimagetool"
fetch "$runtime_url" "$runtime_sha256" "$cache/runtime-x86_64"
chmod +x "$cache/appimagetool"
appdir=$dist/AppDir
mkdir -p "$appdir/usr/bin"
cp "$repo_dir/installer/target/release/shroomvr-installer" "$appdir/usr/bin/"
ln -s usr/bin/shroomvr-installer "$appdir/AppRun"
cp "$repo_dir/installer/shroomvr-installer.desktop" "$appdir/"
cp "$repo_dir/installer/assets/icon.png" "$appdir/shroomvr-installer.png"
ARCH=x86_64 "$cache/appimagetool" --appimage-extract-and-run --no-appstream --runtime-file "$cache/runtime-x86_64" \
    "$appdir" "$dist/ShroomVR-Installer-x86_64.AppImage" >/dev/null

# What the installer reads: this release, the BepInEx it needs and the game versions it was tested with.
python3 - "$dist/$zip_name" "$url_base/$zip_name" "$version" "$repo_dir/game-versions.json" \
    "$bepinex_version" "$bepinex_url" "$bepinex_sha256" > "$dist/manifest.json" <<'PY'
import hashlib, json, os, sys
zip_path, url, version, games, bep_version, bep_url, bep_sha = sys.argv[1:]
data = open(zip_path, "rb").read()
json.dump({
    "schema": 1,
    "version": version,
    "package": {"url": url, "sha256": hashlib.sha256(data).hexdigest(), "size": len(data)},
    "bepinex": {"version": bep_version, "url": bep_url, "sha256": bep_sha},
    "game_versions": json.load(open(games)),
}, sys.stdout, indent=2)
print()
PY
rm -rf "$package" "$appdir"
ls -l "$dist"
