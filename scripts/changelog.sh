#!/usr/bin/env bash
# Genera el changelog (Markdown) de un release a partir de los Conventional Commits entre el tag anterior y el actual.
#
# Uso:  scripts/changelog.sh [--repo dueño/repositorio] <hasta> [<desde>]
#
#   <hasta>   Tag (o cualquier referencia, como HEAD en una corrida de prueba) del release.
#   <desde>   Tag anterior. Si se omite, es el tag v* más reciente anterior a <hasta>; si no hay ninguno, todo el historial.
#   --repo    Para armar los enlaces. Por defecto, GITHUB_REPOSITORY o el remoto "origin".
#
# Los commits de merge se omiten: lo que cuenta son los commits de la rama. Las secciones vacías no se imprimen.
set -euo pipefail

repo="${GITHUB_REPOSITORY:-}"
if [[ "${1:-}" == "--repo" ]]; then
  repo="${2:?--repo necesita un valor}"
  shift 2
fi

to="${1:?Uso: changelog.sh [--repo dueño/repositorio] <hasta> [<desde>]}"
from="${2:-}"

if [[ -z "$repo" ]]; then
  repo="$(git remote get-url origin 2>/dev/null | sed -E 's#^(git@github\.com:|https://github\.com/)##; s#\.git$##' || true)"
fi

if [[ -z "$from" ]]; then
  # Si <hasta> es justo un tag v*, el anterior se busca desde su padre; si no, desde la propia referencia.
  if git describe --exact-match --tags --match 'v*' "$to" >/dev/null 2>&1; then
    base="$to^"
  else
    base="$to"
  fi
  from="$(git describe --tags --abbrev=0 --match 'v*' "$base" 2>/dev/null || true)"
fi

range="$to"
[[ -n "$from" ]] && range="$from..$to"

declare -A sections=(
  [breaking]="" [feat]="" [fix]="" [refactor]="" [perf]="" [docs]="" [test]="" [maintenance]="" [other]=""
)

# Texto de cada línea: "- **ámbito:** Descripción ([`abc1234`](enlace))"
add_entry() {
  local section="$1" scope="$2" description="$3" sha="$4"
  local short="${sha:0:7}" line="- "
  [[ -n "$scope" ]] && line+="**${scope}:** "
  line+="${description^}"
  if [[ -n "$repo" ]]; then
    line+=" ([\`${short}\`](https://github.com/${repo}/commit/${sha}))"
  else
    line+=" (\`${short}\`)"
  fi
  sections[$section]+="${line}"$'\n'
}

conventional='^([a-z]+)(\(([^)]+)\))?(!)?: (.+)$'

while IFS= read -r -d $'\x1e' record; do
  record="${record#$'\n'}"
  [[ -z "$record" ]] && continue
  sha="${record%%$'\x1f'*}"
  rest="${record#*$'\x1f'}"
  subject="${rest%%$'\x1f'*}"
  body="${rest#*$'\x1f'}"

  if [[ "$subject" =~ $conventional ]]; then
    type="${BASH_REMATCH[1]}"
    scope="${BASH_REMATCH[3]}"
    bang="${BASH_REMATCH[4]}"
    description="${BASH_REMATCH[5]}"
  else
    type="" scope="" bang="" description="$subject"
  fi

  case "$type" in
    feat) section=feat ;;
    fix) section=fix ;;
    refactor) section=refactor ;;
    perf) section=perf ;;
    docs) section=docs ;;
    test) section=test ;;
    chore | ci | build | style) section=maintenance ;;
    *) section=other ;;
  esac

  if [[ -n "$bang" ]] || grep -qE '^BREAKING[ -]CHANGE:' <<<"$body"; then
    add_entry breaking "$scope" "$description" "$sha"
  else
    add_entry "$section" "$scope" "$description" "$sha"
  fi
done < <(git log --no-merges --format='%H%x1f%s%x1f%b%x1e' "$range")

print_section() {
  local key="$1" title="$2"
  if [[ -n "${sections[$key]}" ]]; then
    printf '## %s\n\n%s\n' "$title" "${sections[$key]}"
  fi
}

all_sections="${sections[*]}"
if [[ -z "${all_sections//[[:space:]]/}" ]]; then
  printf 'Sin cambios registrados en este rango.\n\n'
fi

print_section breaking "Cambios incompatibles"
print_section feat "Funcionalidades"
print_section fix "Correcciones"
print_section perf "Rendimiento"
print_section refactor "Refactorizaciones"
print_section docs "Documentación"
print_section test "Pruebas"
print_section maintenance "Mantenimiento"
print_section other "Otros cambios"

if [[ -n "$repo" && -n "$from" ]]; then
  printf '**Historial completo:** https://github.com/%s/compare/%s...%s\n' "$repo" "$from" "$to"
fi
