#!/usr/bin/env bash
# Regenerates Source/TargetFilter/Generated from TargetFilter.gram with the DotGram commit and the .NET SDK in dotgram.pin.
#
#     Source/TargetFilter/regenerate.sh
#
# It builds DotGram.Cli from the pinned commit into a scratch folder and runs project-mode `generate` over
# Source/AspectGenerator.csproj. It refuses to run with any other SDK than the pinned one: select that SDK first (a
# global.json for it, or an environment where it is the default). Running it twice changes nothing.
#
# Environment:
#   DOTGRAM_REPO     where to fetch DotGram from (default https://github.com/dotgram/dotgram.git; a local clone works)
#   DOTGRAM_SCRATCH  the scratch folder (default $TMPDIR/aspectgenerator-dotgram-<commit>); kept between runs
#
# Works in Git Bash on Windows as well.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
pin="$here/dotgram.pin"

pinned() {
	local value
	value="$(sed -n "s/^$1=//p" "$pin" | tr -d '\r' | head -n 1)"
	[ -n "$value" ] || { echo "regenerate.sh: $pin has no '$1='" >&2; exit 2; }
	printf '%s' "$value"
}

native() {
	# A path as the dotnet host reads it: Windows form under Git Bash, unchanged elsewhere.
	if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi
}

commit="$(pinned commit)"
sdk="$(pinned sdk)"

cd "$root"

actual="$(dotnet --version)"
if [ "$actual" != "$sdk" ]; then
	echo "regenerate.sh: .NET SDK $actual is selected in $root; the generated files are produced with exactly $sdk (Source/TargetFilter/dotgram.pin)." >&2
	echo "  Install $sdk and select it, for example with a global.json:" >&2
	echo "    { \"sdk\": { \"version\": \"$sdk\", \"rollForward\": \"disable\" } }" >&2
	exit 1
fi

export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

repo="${DOTGRAM_REPO:-https://github.com/dotgram/dotgram.git}"
scratch="${DOTGRAM_SCRATCH:-${TMPDIR:-/tmp}/aspectgenerator-dotgram-${commit:0:12}}"
mkdir -p "$scratch"
scratch="$(cd "$scratch" && pwd)"
tree="$scratch/dotgram"

if [ "$(git -C "$tree" rev-parse HEAD 2> /dev/null || true)" != "$commit" ]; then
	rm -rf "$tree"
	mkdir -p "$tree"
	git -C "$tree" init --quiet
	git -C "$tree" fetch --quiet --depth 1 "$repo" "$commit"
	git -C "$tree" checkout --quiet --detach FETCH_HEAD
	[ "$(git -C "$tree" rev-parse HEAD)" = "$commit" ] || { echo "regenerate.sh: fetched $(git -C "$tree" rev-parse HEAD), not $commit" >&2; exit 1; }
fi

cli="$tree/.build/bin/DotGram.Cli/release/dotnet-dotgram.dll"
stamp="$scratch/cli-$commit-sdk-$sdk.built"
if [ ! -e "$stamp" ] || [ ! -e "$cli" ]; then
	rm -f "$stamp"
	( cd "$tree" && dotnet build src/DotGram.Cli/DotGram.Cli.csproj --configuration Release -nodeReuse:false --verbosity quiet )
	[ -e "$cli" ] || { echo "regenerate.sh: DotGram.Cli was not built at $cli" >&2; exit 1; }
	: > "$stamp"
fi

# The tool reads a restored project and never restores.
dotnet restore Source/AspectGenerator.csproj --verbosity quiet

dotnet "$(native "$cli")" generate Source/AspectGenerator.csproj --output Source/TargetFilter/Generated
