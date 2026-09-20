#!/usr/bin/env bash
#
# Version parity: the minor is the contract.
#
# 0.19.x in any Utos repo means "implements spec 0.19", and the patch belongs to the repo. That
# invariant lived only in prose until this script, which made it exactly one typo away from being
# silently false — a changelog heading naming 0.20.0 against a spec still at 0.19, or an
# implementation of 0.20 released as 0.19.4, and nothing anywhere would have failed.
#
# Checked at PR time rather than at release time, because at release time the mistake is a tag and
# a published artifact that cannot be taken back.
#
# Run it locally with: scripts/check-version-parity.sh

set -euo pipefail

cd "$(dirname "$0")/.."

fail() { echo "error: $*" >&2; exit 1; }

# --- the spec this repo implements ------------------------------------------------------------
[ -f SPEC_VERSION ] || fail "SPEC_VERSION is missing. It names the spec version this repo implements."

SPEC=$(tr -d '[:space:]' < SPEC_VERSION)
[ -n "$SPEC" ] || fail "SPEC_VERSION is empty."

echo "$SPEC" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+$' \
  || fail "SPEC_VERSION must be a three-part version, got '$SPEC'."

SPEC_MINOR=$(echo "$SPEC" | cut -d. -f1-2)

# --- the version this repo is about to call itself ---------------------------------------------
# The topmost heading that names a version. `## [Unreleased]` is deliberately skipped: notes
# accumulate there before anyone has decided what to call them.
RELEASE=$(grep -m1 -oE '^## \[[0-9]+\.[0-9]+\.[0-9]+' CHANGELOG.md | sed 's/^## \[//' || true)
[ -n "$RELEASE" ] || fail "No version heading found in CHANGELOG.md."

RELEASE_MINOR=$(echo "$RELEASE" | cut -d. -f1-2)

if [ "$RELEASE_MINOR" != "$SPEC_MINOR" ]; then
  fail "CHANGELOG.md names $RELEASE but SPEC_VERSION says this repo implements $SPEC.
       The minor is the contract: $RELEASE_MINOR.x must mean 'implements spec $RELEASE_MINOR'.
       Either the version is wrong, or SPEC_VERSION was not updated when the spec moved."
fi

# --- and the Utos packages it depends on -------------------------------------------------------
# A 0.19.0 release depending on 0.20.x packages, or the reverse, claims a spec line its own
# dependencies contradict. Skipped where there are none: utos/sdk-dotnet builds them.
if [ -f Directory.Packages.props ] && grep -q 'Include="Utos\.' Directory.Packages.props; then
  while read -r pin; do
    NAME=$(echo "$pin" | sed 's/.*Include="\([^"]*\)".*/\1/')
    VERSION=$(echo "$pin" | sed 's/.*Version="\([^"]*\)".*/\1/')
    PIN_MINOR=$(echo "$VERSION" | cut -d. -f1-2)

    if [ "$PIN_MINOR" != "$SPEC_MINOR" ]; then
      fail "$NAME is pinned at $VERSION, but this repo implements spec $SPEC.
       A package on a different minor is on a different spec line."
    fi
  done < <(grep -oE 'Include="Utos\.[^"]*" Version="[^"]*"' Directory.Packages.props)
fi

echo "version parity ok: spec $SPEC, release $RELEASE"
