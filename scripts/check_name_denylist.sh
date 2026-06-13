#!/usr/bin/env bash
# name-denylist gate — keeps the public repo free of confidential internal names.
#
# navigaT-SQL began life entangled with internal portfolio data; the working tree was
# scrubbed for the public release. This gate fails the build if any forbidden client /
# portfolio-company name reappears in a tracked file (e.g. accidentally pasted from
# notes or memory), so re-introduction is caught mechanically rather than by vigilance.
#
# Run locally with:  bash scripts/check_name_denylist.sh
#
# Pure grep — no .NET toolchain needed, so it runs as its own fast job.
set -euo pipefail

# Case-insensitive tokens that must never appear in tracked files.
# NB: the PUBLISHER name "GrowthCurve Capital" (LICENSE copyright, csproj <Authors>) is
# intentionally NOT listed — it is public. Only *portfolio-company* names are forbidden.
pattern='hotstats|duetto|revecore|netchex|acciclaim|bottomline|dve/portfolio'

# This script necessarily contains the tokens, so exclude it from its own scan.
# `git grep` exits 0 when it finds matches, 1 when it finds none.
if matches=$(git grep -I -n -i -E "$pattern" -- . ':!scripts/check_name_denylist.sh'); then
  echo "name-denylist: FAIL — forbidden internal name(s) found in tracked files:" >&2
  echo "$matches" >&2
  echo >&2
  echo "These must not appear in the public repo. Remove them before committing." >&2
  exit 1
fi

echo "name-denylist: clean — no forbidden internal names in tracked files."
