#!/usr/bin/env bash
#
# Build the DACPAC and publish it to a target SQL Server / Azure SQL.
#
# The connection string is read from the DB_CONNECTION environment variable so
# that no credential is ever committed. Example for the local Docker instance:
#
#   export DB_CONNECTION='Server=localhost;Database=PlaygroundDatabase;User ID=sa;Password=*****;TrustServerCertificate=True;'
#   ./deploy.sh
#
# Optional first arg: "script" to only GENERATE the migration .sql (review before
# applying) instead of publishing directly.
#
#   ./deploy.sh script
#
set -euo pipefail

cd "$(dirname "$0")"

CONFIG="Release"
DACPAC="bin/${CONFIG}/WebApiPlayground.Database.dacpac"
PROFILE="WebApiPlayground.Database.publish.xml"
ACTION="${1:-publish}"

if ! command -v sqlpackage >/dev/null 2>&1; then
    echo "ERROR: 'sqlpackage' not found. Install it once with:"
    echo "    dotnet tool install -g Microsoft.SqlPackage"
    echo "    (ensure ~/.dotnet/tools is on your PATH)"
    exit 1
fi

if [[ -z "${DB_CONNECTION:-}" ]]; then
    echo "ERROR: set DB_CONNECTION to the target connection string, e.g.:"
    echo "    export DB_CONNECTION='Server=localhost;Database=PlaygroundDatabase;User ID=sa;Password=*****;TrustServerCertificate=True;'"
    exit 1
fi

echo "==> Building DACPAC ($CONFIG)..."
dotnet build WebApiPlayground.Database.sqlproj -c "$CONFIG" -v minimal

case "$ACTION" in
  publish)
    echo "==> Publishing DACPAC to target..."
    sqlpackage /Action:Publish \
      /SourceFile:"$DACPAC" \
      /Profile:"$PROFILE" \
      /TargetConnectionString:"$DB_CONNECTION"
    echo "==> Done."
    ;;
  script)
    OUT="bin/${CONFIG}/WebApiPlayground.Database.DeployScript.sql"
    echo "==> Generating deployment script (no changes applied)..."
    sqlpackage /Action:Script \
      /SourceFile:"$DACPAC" \
      /Profile:"$PROFILE" \
      /TargetConnectionString:"$DB_CONNECTION" \
      /OutputPath:"$OUT"
    echo "==> Script written to $OUT"
    ;;
  *)
    echo "Unknown action '$ACTION'. Use 'publish' (default) or 'script'."
    exit 1
    ;;
esac
