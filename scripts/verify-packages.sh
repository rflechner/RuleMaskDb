#!/usr/bin/env bash
set -euo pipefail

version="${1:?Usage: verify-packages.sh VERSION PACKAGE_DIRECTORY}"
packages="$(cd "${2:?Package directory required}" && pwd)"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Install from the produced archive, without any previously cached tool package.
export NUGET_PACKAGES="$work/nuget-cache"
dotnet tool install RuleMaskDb.Tool --version "$version" --tool-path "$work/tools" --add-source "$packages"
"$work/tools/rulemaskdb" --help
"$work/tools/rulemaskdb" generators

# Compile a real plugin against the NuGet SDK rather than a project reference.
dotnet new classlib -f net10.0 -o "$work/plugin"
rm "$work/plugin/Class1.cs"
dotnet add "$work/plugin/plugin.csproj" package RuleMaskDb.Sdk --version "$version" --source "$packages"
cp "$root"/src/RuleMaskDb/RuleMaskDb.LocalEmailPlugin/*.cs "$work/plugin/"
dotnet publish "$work/plugin/plugin.csproj" -c Release -p:EnableDynamicLoading=true -o "$work/published-plugin"
RULEMASK_PLUGINS_DIRECTORY="$work/published-plugin" "$work/tools/rulemaskdb" plugins > "$work/plugins.txt"
cat "$work/plugins.txt"
grep -F 'LocalEmail' "$work/plugins.txt"
