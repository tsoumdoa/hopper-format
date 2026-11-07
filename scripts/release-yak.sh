#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage:
  ./scripts/release-yak.sh VERSION [--push-test | --push-public | --install] [--allow-dirty]

Examples:
  ./scripts/release-yak.sh 0.1.0
  ./scripts/release-yak.sh 0.1.0 --allow-dirty --install
  ./scripts/release-yak.sh 0.1.0-beta.1 --push-test

Builds a local package by default. Publishing requires an explicit push flag.
--install replaces the same installed version locally and cannot be combined with a push flag.
USAGE
}

log() {
  printf '[release-yak] %s\n' "$*"
}

fail() {
  printf '[release-yak] error: %s\n' "$*" >&2
  exit 1
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

version=""
push_target=""
allow_dirty=0
install_local=0

while (($#)); do
  case "$1" in
    --push-test)
      [[ -z "$push_target" ]] || fail "choose only one publish target"
      push_target="test"
      ;;
    --push-public)
      [[ -z "$push_target" ]] || fail "choose only one publish target"
      push_target="public"
      ;;
    --install)
      install_local=1
      ;;
    --allow-dirty)
      allow_dirty=1
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    -*)
      fail "unknown option: $1"
      ;;
    *)
      [[ -z "$version" ]] || fail "only one VERSION argument is allowed"
      version="$1"
      ;;
  esac
  shift
done

[[ -n "$version" ]] || { usage; fail "VERSION is required"; }
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] || fail "VERSION must be SemVer like 0.1.0 or 0.1.0-beta.1"
[[ "$install_local" -eq 0 || -z "$push_target" ]] || fail "--install cannot be combined with --push-test or --push-public"

if [[ -n "${YAK_BIN:-}" ]]; then
  yak_bin="$YAK_BIN"
elif command -v yak >/dev/null 2>&1; then
  yak_bin="$(command -v yak)"
elif [[ -x "/Applications/Rhino 8.app/Contents/Resources/bin/yak" ]]; then
  yak_bin="/Applications/Rhino 8.app/Contents/Resources/bin/yak"
elif [[ -x "/c/Program Files/Rhino 8/System/Yak.exe" ]]; then
  yak_bin="/c/Program Files/Rhino 8/System/Yak.exe"
else
  fail "Yak CLI not found. Set YAK_BIN, add yak to PATH, or install Rhino 8."
fi

[[ -x "$yak_bin" ]] || fail "Yak CLI is not executable: $yak_bin"

if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  if git rev-parse --verify HEAD >/dev/null 2>&1; then
    if [[ "$allow_dirty" -eq 0 && -n "$(git status --porcelain)" ]]; then
      fail "working tree is dirty; commit/stash changes or pass --allow-dirty"
    fi
  else
    log "Git repository has no commits yet; skipping clean working tree check."
  fi
fi

csproj="HopperFormat.csproj"
solver_project="tests/HopperFormat.SolverTests/HopperFormat.SolverTests.csproj"
shortcut_project="tests/HopperFormat.ShortcutTests/HopperFormat.ShortcutTests.csproj"
manifest="yak/manifest.yml"
readme="README.md"
icon="yak/icon.png"
license="LICENSE"

for source_file in "$csproj" "$solver_project" "$shortcut_project" "$manifest" "$readme" "$icon" "$license"; do
  [[ -f "$source_file" ]] || fail "missing $source_file"
done

csproj_version_count="$(grep -Ec '<Version>[^<]+</Version>' "$csproj" || true)"
manifest_version_count="$(grep -Ec '^version:[[:space:]]*[^[:space:]]+' "$manifest" || true)"
[[ "$csproj_version_count" == "1" ]] || fail "expected exactly one <Version> in $csproj, found $csproj_version_count"
[[ "$manifest_version_count" == "1" ]] || fail "expected exactly one top-level version field in $manifest, found $manifest_version_count"

log "Running Release solver tests."
dotnet run --project "$solver_project" -c Release
log "Running Release shortcut tests."
dotnet run --project "$shortcut_project" -c Release

backup_dir="$(mktemp -d)"
restore_on_error=0

cleanup() {
  status=$?
  if [[ "$status" -ne 0 && "$restore_on_error" -eq 1 ]]; then
    cp "$backup_dir/HopperFormat.csproj" "$csproj"
    cp "$backup_dir/manifest.yml" "$manifest"
    log "Restored $csproj and $manifest after failure."
  fi
  rm -rf "$backup_dir"
  exit "$status"
}
trap cleanup EXIT

cp "$csproj" "$backup_dir/HopperFormat.csproj"
cp "$manifest" "$backup_dir/manifest.yml"
restore_on_error=1

log "Updating source versions to $version."
perl -0pi -e "s#<Version>[^<]+</Version>#<Version>$version</Version>#" "$csproj"
perl -0pi -e "s#^version:[[:space:]]*[^[:space:]]+#version: $version#m" "$manifest"

log "Building Release targets."
dotnet build "$csproj" -c Release

artifact_dir="artifacts/yak"
stage_dir="$artifact_dir/stage"
rm -rf "$stage_dir"
mkdir -p "$stage_dir/misc" "$stage_dir/net8.0-windows" "$stage_dir/net8.0"

log "Staging Yak package contents."
cp "$manifest" "$stage_dir/manifest.yml"
cp "$icon" "$stage_dir/icon.png"
cp "$license" "$stage_dir/LICENSE"
cp "$readme" "$stage_dir/misc/README.md"
cp "bin/Release/net8.0-windows/HopperFormat.gha" "$stage_dir/net8.0-windows/HopperFormat.gha"
cp "bin/Release/net8.0/HopperFormat.gha" "$stage_dir/net8.0/HopperFormat.gha"

rm -f "$artifact_dir"/hopperformat-"$version"-*.yak

log "Building Yak package."
(
  cd "$stage_dir"
  "$yak_bin" build --platform any --version "$version"
)

shopt -s nullglob
built_packages=("$stage_dir"/hopperformat-"$version"-*.yak)
shopt -u nullglob
[[ "${#built_packages[@]}" -eq 1 ]] || fail "expected one built Yak package, found ${#built_packages[@]}"

artifact="$artifact_dir/$(basename "${built_packages[0]}")"
mv "${built_packages[0]}" "$artifact"

log "Created $artifact"
log "Package contents:"
unzip -l "$artifact"

if [[ "$install_local" -eq 1 ]]; then
  rhino_running=0
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      tasklist_bin="$(command -v tasklist.exe || command -v tasklist || true)"
      if [[ -n "$tasklist_bin" ]] && MSYS_NO_PATHCONV=1 "$tasklist_bin" /FI "IMAGENAME eq Rhino.exe" /NH | grep -qi '^Rhino\.exe[[:space:]]'; then
        rhino_running=1
        log "Warning: Rhino is running. Restart Rhino to load the new build."
      fi
      ;;
  esac

  install_source="$(cd "$artifact_dir" && pwd)"
  if command -v cygpath >/dev/null 2>&1; then
    install_source="$(cygpath -w "$install_source")"
  fi

  installed_packages="$("$yak_bin" list)"
  if printf '%s\n' "$installed_packages" | tr -d '\r' | awk -v version="$version" '
    $1 == "hopperformat" && $2 == "(" version ")" { found = 1 }
    END { exit !found }
  '; then
    if [[ "$rhino_running" -eq 1 ]]; then
      fail "Rhino is running and hopperformat $version is already installed; close Rhino and rerun the release command with --install"
    fi
    log "Removing installed hopperformat $version so the rebuilt package replaces it."
    if ! "$yak_bin" uninstall hopperformat; then
      fail "could not uninstall hopperformat; close Rhino and retry the release command with --install"
    fi
  fi

  log "Installing hopperformat $version from $install_source."
  "$yak_bin" install --source "$install_source" hopperformat "$version"
  log "Restart Rhino and open Grasshopper to load the new build."
fi

case "$push_target" in
  test)
    log "Pushing to Yak test server."
    "$yak_bin" push --source https://test.yak.rhino3d.com "$artifact"
    # Publishing cannot be rolled back, even if the follow-up search fails.
    restore_on_error=0
    "$yak_bin" search --source https://test.yak.rhino3d.com --all --prerelease hopperformat
    ;;
  public)
    log "Pushing to public Yak server."
    "$yak_bin" push "$artifact"
    restore_on_error=0
    ;;
esac

restore_on_error=0
