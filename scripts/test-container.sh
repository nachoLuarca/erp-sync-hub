#!/usr/bin/env bash
# Se ejecuta dentro del contenedor del SDK de .NET; lo invoca scripts/test.ps1.
# Trabaja sobre una copia del repositorio para no mezclar los bin/ y obj/ de Linux con los de Windows.
set -euo pipefail

work=/work
mkdir -p "$work"
tar -C /src \
  --exclude=./.git --exclude=./.vs --exclude=./coverage \
  --exclude='*/bin' --exclude='*/obj' \
  -cf - . | tar -C "$work" -xf -
cd "$work"

step() { printf '\n==> %s\n' "$1"; }

step "restore"
dotnet restore

step "build (Release)"
dotnet build -c Release --no-restore

if [ "${SKIP_FORMAT:-0}" != "1" ]; then
  step "format (--verify-no-changes)"
  dotnet format ErpSyncHub.slnx --verify-no-changes --no-restore
fi

step "tests"
if [ -n "${TEST_FILTER:-}" ]; then
  dotnet test -c Release --no-build --filter "$TEST_FILTER"
else
  dotnet test -c Release --no-build
fi
