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

# Docker Desktop publica host.docker.internal con una dirección IPv4 y otra IPv6 (fdc4:...), y esta última no es
# alcanzable desde el contenedor: según cómo se resuelva el nombre, Npgsql intenta conectarse a ella y falla con
# "Network is unreachable". Se usa directamente la dirección IPv4 para que Testcontainers no dependa de esa resolución.
host_ipv4=$(getent ahostsv4 host.docker.internal | awk 'NR==1 { print $1 }' || true)
if [ -n "$host_ipv4" ]; then
  export TESTCONTAINERS_HOST_OVERRIDE="$host_ipv4"
fi

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
