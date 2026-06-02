#!/usr/bin/env bash
#
# Build/lint + deploy dell'infrastruttura Bicep su una subscription Azure.
#
# Idempotente: ARM è dichiarativo, riapplicare lo stesso stato è un no-op.
# Default = `whatif`: vedi sempre il diff PRIMA di applicare (come `deploy.sh
# script` del database).
#
#   AZURE_SUBSCRIPTION_ID=<sub-guid> ./deploy.sh            # what-if (anteprima)
#   AZURE_SUBSCRIPTION_ID=<sub-guid> ./deploy.sh deploy     # applica
#
# Variabili d'ambiente:
#   AZURE_SUBSCRIPTION_ID  (obbligatoria)  subscription target
#   AZURE_LOCATION         (default westeurope)  region del deployment
#   ENV                    (default dev)          dev | prod → sceglie il .bicepparam
#
# Override dei parametri non committati (es. principal ID):
#   PARAMS="adminPrincipalId=<objId> appPrincipalId=<objId>" ./deploy.sh deploy
#
set -euo pipefail

cd "$(dirname "$0")"

ACTION="${1:-whatif}"
ENV="${ENV:-dev}"
LOCATION="${AZURE_LOCATION:-westeurope}"
TEMPLATE="main.bicep"
PARAM_FILE="main.${ENV}.bicepparam"

if ! command -v az >/dev/null 2>&1; then
    echo "ERROR: 'az' (Azure CLI) non trovato. Installalo da:"
    echo "    https://learn.microsoft.com/cli/azure/install-azure-cli"
    exit 1
fi

if [[ ! -f "$PARAM_FILE" ]]; then
    echo "ERROR: param file '$PARAM_FILE' inesistente (ENV='$ENV'). Usa ENV=dev|prod."
    exit 1
fi

if [[ -z "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
    echo "ERROR: imposta AZURE_SUBSCRIPTION_ID alla subscription target, es.:"
    echo "    export AZURE_SUBSCRIPTION_ID='00000000-0000-0000-0000-000000000000'"
    exit 1
fi

# Override parametri opzionali (key=value separati da spazio).
EXTRA_PARAMS=()
if [[ -n "${PARAMS:-}" ]]; then
    # shellcheck disable=SC2206
    EXTRA_PARAMS=($PARAMS)
fi

echo "==> Lint/compile Bicep ($TEMPLATE)..."
az bicep build --file "$TEMPLATE" --stdout >/dev/null

DEPLOYMENT_NAME="webapiplay-${ENV}-$(date +%Y%m%d%H%M%S)"

case "$ACTION" in
  whatif)
    echo "==> what-if su subscription $AZURE_SUBSCRIPTION_ID (location $LOCATION, env $ENV)..."
    az deployment sub what-if \
      --subscription "$AZURE_SUBSCRIPTION_ID" \
      --location "$LOCATION" \
      --template-file "$TEMPLATE" \
      --parameters "$PARAM_FILE" \
      ${EXTRA_PARAMS[@]+"${EXTRA_PARAMS[@]}"}
    ;;
  deploy)
    echo "==> Deploy su subscription $AZURE_SUBSCRIPTION_ID (location $LOCATION, env $ENV)..."
    az deployment sub create \
      --name "$DEPLOYMENT_NAME" \
      --subscription "$AZURE_SUBSCRIPTION_ID" \
      --location "$LOCATION" \
      --template-file "$TEMPLATE" \
      --parameters "$PARAM_FILE" \
      ${EXTRA_PARAMS[@]+"${EXTRA_PARAMS[@]}"}
    echo "==> Done."
    ;;
  *)
    echo "Azione '$ACTION' sconosciuta. Usa 'whatif' (default) o 'deploy'."
    exit 1
    ;;
esac
