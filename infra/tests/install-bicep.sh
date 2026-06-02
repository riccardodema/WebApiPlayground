#!/usr/bin/env bash
#
# Scarica il binario standalone della Bicep CLI in `.tools/bicep` (gitignored),
# così gli unit test IaC (tests/WebApiPlayground.IacTests) girano in locale senza
# installare l'Azure CLI né toccare il PATH: il test helper lo trova lì in automatico.
#
#   ./infra/tests/install-bicep.sh           # installa (skip se già presente)
#   ./infra/tests/install-bicep.sh --force   # ri-scarica l'ultima versione
#
# Alternativa: `az bicep install` (se hai l'Azure CLI) oppure imposta BICEP_CLI_PATH
# a un binario bicep esistente.
#
set -euo pipefail

# Repo root = due livelli sopra questo script (infra/tests/ -> repo).
cd "$(dirname "$0")/../.."

TOOLS_DIR=".tools"
DEST="${TOOLS_DIR}/bicep"
FORCE="${1:-}"

if [[ -x "$DEST" && "$FORCE" != "--force" ]]; then
    echo "Bicep già presente: $("$DEST" --version)"
    echo "(usa --force per ri-scaricare)"
    exit 0
fi

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
    Darwin) platform="osx" ;;
    Linux)  platform="linux" ;;
    *) echo "OS non supportato: $os. Usa 'az bicep install' o imposta BICEP_CLI_PATH."; exit 1 ;;
esac

case "$arch" in
    arm64|aarch64) cpu="arm64" ;;
    x86_64|amd64)  cpu="x64" ;;
    *) echo "Architettura non supportata: $arch."; exit 1 ;;
esac

asset="bicep-${platform}-${cpu}"
url="https://github.com/Azure/bicep/releases/latest/download/${asset}"

mkdir -p "$TOOLS_DIR"
echo "==> Download ${asset} dall'ultima release..."
curl -fsSL -o "$DEST" "$url"
chmod +x "$DEST"

echo "==> Installato: $("$DEST" --version)"
echo "    Path: $(pwd)/${DEST}"
echo "    Ora: dotnet test tests/WebApiPlayground.IacTests/WebApiPlayground.IacTests.csproj"
