#!/usr/bin/env bash
# Creates/updates the schema and grants the Function App's managed identity access.
# Uses Microsoft Entra authentication (the server has no SQL logins). Requires go-sqlcmd
# (https://aka.ms/go-sqlcmd) and `az login` as the SQL Entra admin (or a member of the admin group).
# Your client IP must be allowed by the SQL server firewall.
#
# Required env vars:
#   SQL_SERVER         server FQDN (deployment output sqlServerFqdn)
#   SQL_DATABASE       database name (deployment output sqlDatabaseName)
#   FUNCTION_APP_NAME  Function App name (deployment output functionAppName)
# Optional:
#   INIT_SQL_SKIP_GRANT=true  apply the schema only. The GitHub deploy workflow sets this: creating the
#                             app's database user resolves its name in Entra ID, which a service principal
#                             can only do if the SQL server has Directory Readers. Run the grant once as a person.
set -euo pipefail
SQLCMD=${SQLCMD:-sqlcmd}
: "${SQL_SERVER:?set SQL_SERVER}"; : "${SQL_DATABASE:?set SQL_DATABASE}"; : "${FUNCTION_APP_NAME:?set FUNCTION_APP_NAME}"
AUTH=(--authentication-method ActiveDirectoryDefault)
for f in collector/src/ChromeCollector.FunctionApp/Sql/001_tables.sql collector/src/ChromeCollector.FunctionApp/Sql/002_views.sql collector/src/ChromeCollector.FunctionApp/Sql/003_procedures.sql; do
  echo "Applying $f"
  $SQLCMD -S "$SQL_SERVER" -d "$SQL_DATABASE" "${AUTH[@]}" -b -i "$f"
done
if [[ "${INIT_SQL_SKIP_GRANT:-false}" == "true" ]]; then
  echo "Skipping grant-function-identity.sql (INIT_SQL_SKIP_GRANT=true)"
  exit 0
fi
echo "Granting database access to managed identity '$FUNCTION_APP_NAME'"
$SQLCMD -S "$SQL_SERVER" -d "$SQL_DATABASE" "${AUTH[@]}" -b -v FunctionAppName="$FUNCTION_APP_NAME" -i infra/scripts/sql/grant-function-identity.sql
