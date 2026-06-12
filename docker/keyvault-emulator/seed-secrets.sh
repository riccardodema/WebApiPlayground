#!/bin/sh
# Seed dei SEGRETI nel Key Vault emulato (job one-shot `keyvault-seed` di docker-compose), via REST —
# la stessa API del cloud (PUT /secrets/{name}?api-version=7.4). Dopo questo job l'api carica i secret
# dal vault all'avvio (config provider): le connection string NON stanno più nell'env dell'api.
# In Azure l'equivalente di questo script è ./infra/set-secrets.sh (az keyvault secret set).
#
# - bearer: l'emulatore non valida firma/claim, basta un JWT ben formato → lo si minta qui in shell
#   (stesso approccio della KeyVaultEmulatorCredential dell'app; nessun endpoint ausiliario).
# - curl -k: certificato self-signed dell'emulatore, fidato SOLO per queste chiamate di sviluppo.
set -eu

VAULT="https://keyvault:4997"

b64url() { printf '%s' "$1" | base64 | tr -d '=\n' | tr '+/' '-_'; }
TOKEN="$(b64url '{"alg":"HS256","typ":"JWT"}').$(b64url '{"iss":"https://keyvault-emulator.local","aud":"https://vault.azure.net","exp":253402300799}').$(b64url emulator)"

# L'healthcheck del servizio keyvault è solo TCP: qui si attende che l'API REST risponda davvero.
tries=0
until curl -ksf -o /dev/null --max-time 5 "$VAULT/secrets?api-version=7.4" -H "Authorization: Bearer $TOKEN"; do
  tries=$((tries + 1))
  if [ "$tries" -ge 30 ]; then
    echo "Key Vault emulator non raggiungibile dopo $tries tentativi." >&2
    exit 1
  fi
  sleep 2
done

put_secret() {
  curl -ksf -X PUT "$VAULT/secrets/$1?api-version=7.4" \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    --data "{\"value\":\"$2\"}" > /dev/null
  echo "Secret '$1' impostato."
}

# NB: il valore finisce in un JSON inline → MSSQL_SA_PASSWORD non deve contenere '"' o '\' (vedi .env.example).
put_secret "ConnectionStrings--Default" \
  "Server=db;Database=PlaygroundDatabase;User ID=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True;"

# In compose il broker è l'emulatore ASB → SAS statica nota (UseDevelopmentEmulator). In Azure questo
# secret NON esiste: l'app usa ServiceBus__FullyQualifiedNamespace + managed identity (no SAS).
put_secret "ServiceBus--ConnectionString" \
  "Endpoint=sb://servicebus;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"

echo "Seed del Key Vault emulato completato."
