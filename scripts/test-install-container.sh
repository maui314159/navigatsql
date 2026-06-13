#!/usr/bin/env bash
# Clean-room install test for navigaT-SQL.
#
# Runs the real install paths in pristine Linux containers — Apple `container` by
# default, Docker as a fallback — so no host state (a global SDK config, an existing
# ~/.dotnet/tools, a warm NuGet cache) can mask a packaging bug. It validates the
# shipping channel end to end:
#
#   Phase A — `dotnet tool install --global navigatsql` in a clean .NET SDK container,
#             from a freshly built package on a local feed (a faithful proxy for the
#             real `nuget.org` install), after a from-scratch build + test + pack.
#   Phase B — the documented self-contained single-file binary, run in a bare image
#             with NO .NET installed, proving it needs no runtime on the target.
#
# This is a maintainer-run, pre-release check (GitHub-hosted CI cannot run Apple
# `container`; pass CONTAINER_RUNTIME=docker to run it on a Docker host instead).
#
# Usage:
#   scripts/test-install-container.sh                   # fresh-clone `main` of the public repo
#   REF=v0.1.0 scripts/test-install-container.sh        # a specific tag / branch / sha
#   REPO_DIR="$PWD" scripts/test-install-container.sh   # test a local checkout instead of cloning
#   CONTAINER_RUNTIME=docker scripts/test-install-container.sh
#
# Requires: `container` (or docker) + network access (image pull, git clone, NuGet restore).
set -euo pipefail

RT="${CONTAINER_RUNTIME:-container}"
SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
BARE_IMAGE="${BARE_IMAGE:-docker.io/library/debian:stable-slim}"
REPO_URL="${REPO_URL:-https://github.com/maui314159/navigatsql.git}"
REF="${REF:-main}"
REPO_DIR="${REPO_DIR:-}"
# Apple `container` doesn't reliably forward the gateway resolver into containers, so
# in-container `git clone` + NuGet restore fail DNS. Point it at a public resolver by
# default (Docker manages DNS itself, so this is only applied for `container`).
DNS="${DNS:-8.8.8.8}"

command -v "$RT" >/dev/null 2>&1 || { echo "error: container runtime '$RT' not found (set CONTAINER_RUNTIME)"; exit 2; }

# Build the self-contained binary for the host's native arch so it runs in the
# container without emulation.
case "$(uname -m)" in
  arm64 | aarch64) RID=linux-arm64 ;;
  x86_64 | amd64) RID=linux-x64 ;;
  *) echo "error: unsupported arch $(uname -m)"; exit 2 ;;
esac

NET_ARGS=()
if [ "$RT" = "container" ] && [ -n "$DNS" ]; then NET_ARGS=(--dns "$DNS"); fi

WORK="$(mktemp -d /tmp/navigatsql-itest.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT

MOUNT_SRC=()
if [ -n "$REPO_DIR" ]; then
  SRC_CMD='cp -r /src /work/src'
  MOUNT_SRC=(-v "$REPO_DIR:/src")
  echo "==> source: local checkout $REPO_DIR"
else
  SRC_CMD="git clone --depth 1 --branch $REF $REPO_URL /work/src"
  echo "==> source: fresh clone $REPO_URL @ $REF"
fi
echo "==> runtime=$RT  sdk=$SDK_IMAGE  bare=$BARE_IMAGE  rid=$RID"

# ---------- Phase A: `dotnet tool` install in a pristine SDK container ----------
{
  printf 'SRC_CMD=%q\n' "$SRC_CMD"
  printf 'RID=%q\n' "$RID"
  cat <<'PHASE_A'
set -euo pipefail
command -v git >/dev/null 2>&1 || { apt-get update -qq && apt-get install -y -qq git ca-certificates >/dev/null; }
eval "$SRC_CMD"
cd /work/src
echo "--- build (warnings as errors) ---"; dotnet build navigatsql.csproj -warnaserror
echo "--- test ---";                        dotnet test tests/NavigaTSql.Tests
echo "--- pack ---";                        dotnet pack navigatsql.csproj -c Release -o ./nupkg
echo "--- dotnet tool install --global (local feed = nuget.org proxy) ---"
dotnet tool install --global --add-source "$PWD/nupkg" navigatsql
export PATH="$PATH:$HOME/.dotnet/tools"
echo "--- invoke the installed 'navigatsql' command ---"
navigatsql sample.sql >/work/edges.json 2>/work/summary.txt
grep -q '"reads_table"' /work/edges.json || { echo "FAIL: installed tool produced no reads_table edge"; exit 1; }
echo "--- publish self-contained single-file ($RID, invariant globalization) for Phase B ---"
dotnet publish navigatsql.csproj -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:InvariantGlobalization=true -o /work/publish
cp sample.sql /work/sample.sql
echo "PHASE A OK"
PHASE_A
} >"$WORK/phaseA.sh"

echo
echo "==================== PHASE A — dotnet tool install (clean SDK container) ===================="
"$RT" run --rm ${NET_ARGS[@]+"${NET_ARGS[@]}"} -v "$WORK:/work" ${MOUNT_SRC[@]+"${MOUNT_SRC[@]}"} "$SDK_IMAGE" bash /work/phaseA.sh
echo "==> Phase A produced $(grep -c '"Relation"' "$WORK/edges.json") edge(s); stderr summary:"
tail -n1 "$WORK/summary.txt" | sed 's/^/    /'

# ---------- Phase B: self-contained binary in a bare container (no .NET) ----------
cat <<'PHASE_B' >"$WORK/phaseB.sh"
set -euo pipefail
if command -v dotnet >/dev/null 2>&1; then echo "FAIL: dotnet unexpectedly present in bare image"; exit 1; fi
echo "dotnet present? NO (good — proving the binary is self-contained)"
/work/publish/navigatsql /work/sample.sql >/work/edges2.json 2>/dev/null
grep -q '"reads_table"' /work/edges2.json || { echo "FAIL: self-contained binary produced no reads_table edge"; exit 1; }
echo "PHASE B OK"
PHASE_B

echo
echo "==================== PHASE B — self-contained binary (bare container, no .NET) ===================="
"$RT" run --rm ${NET_ARGS[@]+"${NET_ARGS[@]}"} -v "$WORK:/work" "$BARE_IMAGE" bash /work/phaseB.sh

echo
echo "✅ ALL CLEAN-ROOM INSTALL TESTS PASSED"
