#!/usr/bin/env bash
# Builds the v2 filter grammar spike against the pinned DotGram commit and compares the generated parser with DotGram's
# editor core over corpus.txt, then times both on adversarial inputs.
#
#     Spikes/FilterV2Grammar/run.sh [--editor-root <dotgram checkout>] [--verbose]
#
# The DotGram sources come from the commit in Source/TargetFilter/dotgram.pin, fetched into $SPIKE_SCRATCH (default
# $TMPDIR/aspectgenerator-filter-v2-spike). --editor-root compiles the editor core from another checkout instead, for
# trying a change to DotGram's interpreter; the generator stays the pinned one. Nothing here is part of the build of
# AspectGenerator or of its package.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
commit="$(sed -n 's/^commit=//p' "$root/Source/TargetFilter/dotgram.pin" | tr -d '\r' | head -n 1)"
repo="${DOTGRAM_REPO:-https://github.com/dotgram/dotgram.git}"
scratch="${SPIKE_SCRATCH:-${TMPDIR:-/tmp}/aspectgenerator-filter-v2-spike}"
editor=""
verbose=""

while [ $# -gt 0 ]; do
	case "$1" in
		--editor-root) editor="$2"; shift 2 ;;
		--verbose)     verbose="--verbose"; shift ;;
		*) echo "run.sh: unknown argument $1" >&2; exit 2 ;;
	esac
done

tree="$scratch/dotgram"
mkdir -p "$scratch"

if [ "$(git -C "$tree" rev-parse HEAD 2> /dev/null || true)" != "$commit" ]; then
	rm -rf "$tree"
	mkdir -p "$tree"
	git -C "$tree" init --quiet
	git -C "$tree" fetch --quiet --depth 1 "$repo" "$commit"
	git -C "$tree" checkout --quiet --detach FETCH_HEAD
fi

export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

out="$scratch/bin"
props=(-p:DotGramSpikeRoot="$tree")
[ -n "$editor" ] && props+=(-p:DotGramEditorRoot="$editor")

dotnet build "$here/Harness/FilterV2.Harness.csproj" -c Release -nodeReuse:false -warnaserror "${props[@]}" -o "$out" --verbosity quiet

dotnet "$out/FilterV2.Harness.dll" "$here/Parser/FilterV2.gram" "$here/corpus.txt" --scaling $verbose
