#!/usr/bin/env bash
#
# Imposta un SEGRETO dell'app nel Key Vault creato dall'IaC — l'equivalente cloud del job one-shot
# `keyvault-seed` di docker-compose. I secret NON passano mai per ARM/Bicep (regola "nessun secret
# nell'IaC", vedi README): si impostano fuori banda, qui con `az keyvault secret set`.
#
#   AZURE_SUBSCRIPTION_ID=<sub-guid> ./set-secrets.sh ConnectionStrings--Default
#   AZURE_SUBSCRIPTION_ID=<sub-guid> ENV=prod ./set-secrets.sh ConnectionStrings--Default
#
# Il valore viene chiesto con un prompt SILENZIOSO (mai in argv/history) oppure letto da stdin se
# non interattivo:   printf '%s' "$VALUE" | ./set-secrets.sh ConnectionStrings--Default
#
# Naming: '--' nel nome del secret = ':' nella configuration dell'app
# (ConnectionStrings--Default → ConnectionStrings:Default). Vedi docs/keyvault.md.
#
# Prerequisiti: ruolo RBAC 'Key Vault Secrets Officer' sul vault (param adminPrincipalId del deploy)
# e IP nel firewall del vault (param allowedIpAddresses) — il default è Deny.
#
# Variabili d'ambiente:
#   AZURE_SUBSCRIPTION_ID  (obbligatoria)  subscription del vault
#   ENV                    (default dev)   ambiente → resource group rg-webapiplay-<ENV>
#   KEYVAULT_NAME          (opzionale)     nome del vault; se assente lo scopre dal resource group
#
set -euo pipefail

cd "$(dirname "$0")"

SECRET_NAME="${1:?uso: ./set-secrets.sh <NomeSecret>   (es. ConnectionStrings--Default)}"
ENV="${ENV:-dev}"
RESOURCE_GROUP="rg-webapiplay-${ENV}"

if ! command -v az >/dev/null 2>&1; then
    echo "ERROR: 'az' (Azure CLI) non trovato. Installalo da:"
    echo "    https://learn.microsoft.com/cli/azure/install-azure-cli"
    exit 1
fi

if [[ -z "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
    echo "ERROR: imposta AZURE_SUBSCRIPTION_ID alla subscription target, es.:"
    echo "    export AZURE_SUBSCRIPTION_ID='00000000-0000-0000-0000-000000000000'"
    exit 1
fi

az account set --subscription "$AZURE_SUBSCRIPTION_ID"

# Il nome del vault ha un suffisso uniqueString → si scopre dal resource group (un solo KV per RG).
if [[ -z "${KEYVAULT_NAME:-}" ]]; then
    KEYVAULT_NAME=$(az keyvault list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv)
    if [[ -z "$KEYVAULT_NAME" ]]; then
        echo "ERROR: nessun Key Vault trovato in '$RESOURCE_GROUP'. Hai fatto il deploy? (./deploy.sh deploy)"
        exit 1
    fi
fi

# Valore mai in argv di QUESTO script: prompt silenzioso (tty) o stdin (pipe/CI).
if [[ -t 0 ]]; then
    read -rs -p "Valore del secret '$SECRET_NAME': " SECRET_VALUE
    echo
else
    SECRET_VALUE=$(cat)
fi

if [[ -z "$SECRET_VALUE" ]]; then
    echo "ERROR: valore vuoto: niente da impostare."
    exit 1
fi

echo "==> Imposto '$SECRET_NAME' su '$KEYVAULT_NAME' ($RESOURCE_GROUP)..."
SECRET_ID=$(az keyvault secret set \
    --vault-name "$KEYVAULT_NAME" \
    --name "$SECRET_NAME" \
    --value "$SECRET_VALUE" \
    --query id -o tsv)

echo "OK: $SECRET_ID"
echo "L'app lo legge come '${SECRET_NAME//--/:}' (KeyVault:Uri = https://${KEYVAULT_NAME}.vault.azure.net/)."
