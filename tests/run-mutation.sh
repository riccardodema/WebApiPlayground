#!/usr/bin/env bash
#
# Mutation testing (Stryker.NET) per-progetto, orchestrata: una run per ciascun layer, con i SOLI
# unit test come test bed (mutare con gli integration test — container Docker — costerebbe ore e
# falsa il segnale: i test time-sensitive flakeggiano sotto carico). NIENTE solution mode di
# proposito: arruolerebbe tutti i test project della solution. Vedi .claude/context/testing.md e [L29].
#
#   ./tests/run-mutation.sh                          # run completa (soglia break da stryker-config.json)
#   ./tests/run-mutation.sh --since:origin/main --break-at 60   # incrementale (PR gate)
#
# Output: report html/json per progetto in tests/WebApiPlayground.Tests/StrykerOutput/ +
# punteggio COMBINATO in .../StrykerOutput/combined-score.json (usato dal badge in CI).
set -uo pipefail

cd "$(dirname "$0")/.."   # radice del repo

# Domain è escluso DI PROPOSITO: contiene solo POCO/auto-property (zero logica) — i suoi pochi
# mutanti (default di inizializzazione) non hanno comportamento osservabile e farebbero solo
# rumore nel punteggio. NB: ApiVersioningExtensions.cs è escluso dal mutate in stryker-config.json
# (è il file INTERCETTATO dal source generator OpenAPI: mutarlo rompe il checksum degli
# interceptor → CS9234 → CompilationException). Vedi .claude/lessons.md [L29].
PROJECTS=(
  WebApiPlayground.Application.csproj
  WebApiPlayground.Infrastructure.csproj
  WebApiPlayground.Api.csproj
)

TEST_PROJECT_DIR="tests/WebApiPlayground.Tests"
OUTPUT_DIR="$TEST_PROJECT_DIR/StrykerOutput"

dotnet tool restore >/dev/null
rm -rf "$OUTPUT_DIR"

# Il source generator OpenAPI emette interceptor che la ricompilazione di Stryker non riproduce
# (CS9234 → run abortita): nelle build di mutation lo si disattiva via MSBuild property (le env var
# sono lette come property). Nessun effetto su build normali/CI. Vedi [L29] e Api.csproj.
export DisableOpenApiSourceGenerator=true

overall_rc=0
for project in "${PROJECTS[@]}"; do
  echo "==> Stryker: ${project}"
  (
    cd "$TEST_PROJECT_DIR" &&
    dotnet tool run dotnet-stryker \
      --config-file ../../stryker-config.json \
      --project "$project" \
      --reporter json --reporter html --reporter progress \
      "$@"
  )
  rc=$?
  # Lo stesso exit code non-zero copre sia il break-threshold sia gli errori del tool.
  if [[ $rc -ne 0 ]]; then
    echo "::error::Stryker su ${project} è uscito con codice ${rc}"
    overall_rc=$rc
  fi
done

# Punteggio COMBINATO su tutti i report (detected = Killed+Timeout; undetected = Survived+NoCoverage).
total_detected=0
total_undetected=0
while IFS= read -r report; do
  d=$(jq '[.files[].mutants[] | select(.status=="Killed" or .status=="Timeout")] | length' "$report")
  u=$(jq '[.files[].mutants[] | select(.status=="Survived" or .status=="NoCoverage")] | length' "$report")
  total_detected=$((total_detected + d))
  total_undetected=$((total_undetected + u))
done < <(find "$OUTPUT_DIR" -name mutation-report.json 2>/dev/null)

if (( total_detected + total_undetected > 0 )); then
  score=$(awk -v d="$total_detected" -v u="$total_undetected" 'BEGIN { printf "%.1f", 100 * d / (d + u) }')
else
  score="" # incrementale senza file mutabili cambiati: nessun punteggio da riportare
fi

mkdir -p "$OUTPUT_DIR"
printf '{"detected":%d,"undetected":%d,"score":"%s"}\n' \
  "$total_detected" "$total_undetected" "$score" > "$OUTPUT_DIR/combined-score.json"

echo
if [[ -n "$score" ]]; then
  echo "Mutation score COMBINATO: ${score}%  (detected: ${total_detected}, undetected: ${total_undetected})"
else
  echo "Nessun mutante generato (nessun file mutabile nel diff?): niente punteggio."
fi

exit $overall_rc
