#!/usr/bin/env bash
# Se ejecuta dentro del contenedor del SDK de .NET; lo invoca scripts/repetir-tests.ps1.
# Compila una vez y repite los tests del filtro con hilos ocupados, para sacar a la luz las pruebas inestables.
set -uo pipefail

work=/work
mkdir -p "$work"
tar -C /src \
  --exclude=./.git --exclude=./.vs --exclude=./coverage \
  --exclude='*/bin' --exclude='*/obj' \
  -cf - . | tar -C "$work" -xf -
cd "$work"

# Docker Desktop publica host.docker.internal con una dirección IPv6 inalcanzable (ver test-container.sh).
host_ipv4=$(getent ahostsv4 host.docker.internal | awk 'NR==1 { print $1 }' || true)
if [ -n "$host_ipv4" ]; then
  export TESTCONTAINERS_HOST_OVERRIDE="$host_ipv4"
fi

export ERPSYNC_TESTS_CONTAINER_NETWORK=1 # ver test-container.sh

dotnet restore > /dev/null || exit 1
dotnet build -c Release --no-restore 2>&1 | tail -3

for _ in $(seq 1 "${BURNERS:-4}"); do
  ( while :; do :; done ) &
done

failures=0
for n in $(seq 1 "${REPS:-50}"); do
  output=$(dotnet test "${TEST_PROJECT:-tests/ErpSyncHub.IntegrationTests}" -c Release --no-build ${TEST_FILTER:+--filter "$TEST_FILTER"} 2>&1)
  if echo "$output" | grep -q "Failed!"; then
    failures=$((failures + 1))
    echo "=== repetición $n: FALLÓ"
    echo "$output" | grep -E "^\s+Failed |Error Message|^\s{3}\S" | head -40
  else
    echo "repetición $n: ok"
  fi
done

echo "repeticiones: ${REPS:-50}, fallos: $failures"
[ "$failures" -eq 0 ]
