#!/usr/bin/env bash
# Adds Microsoft Entra groups to the Chromebook database roles (docs/sql-access.md).
# Run as a person who is the SQL Entra admin (or in the admin group), after the schema has been applied.
# Requires go-sqlcmd and `az login`; your client IP must be allowed by the SQL server firewall.
#
# Required env vars:
#   SQL_SERVER    server FQDN (deployment output sqlServerFqdn)
#   SQL_DATABASE  database name (deployment output sqlDatabaseName)
# At least one of (the Entra group's display name):
#   DEVICE_READERS_GROUP   helpdesk / ops
#   INVESTIGATORS_GROUP    IR analysts
#   AUDIT_REVIEWERS_GROUP  IR lead / compliance
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SQLCMD=${SQLCMD:-sqlcmd}
if ! command -v "$SQLCMD" >/dev/null 2>&1; then
  echo "sqlcmd not found. Install go-sqlcmd with: bash infra/scripts/install-sqlcmd.sh (see docs/sql-access.md)." >&2
  exit 1
fi
: "${SQL_SERVER:?set SQL_SERVER}"; : "${SQL_DATABASE:?set SQL_DATABASE}"
if [[ -z "${DEVICE_READERS_GROUP:-}${INVESTIGATORS_GROUP:-}${AUDIT_REVIEWERS_GROUP:-}" ]]; then
  echo "Set at least one of DEVICE_READERS_GROUP, INVESTIGATORS_GROUP, AUDIT_REVIEWERS_GROUP." >&2
  exit 2
fi
# Repo layout (infra/scripts/sql/), or the .sql copied into the same folder as this script.
SQL_FILE=""
for candidate in "$SCRIPT_DIR/sql/grant-roles.sql" "$SCRIPT_DIR/grant-roles.sql"; do
  [[ -f "$candidate" ]] && { SQL_FILE="$candidate"; break; }
done
if [[ -z "$SQL_FILE" ]]; then
  echo "grant-roles.sql not found. Looked in $SCRIPT_DIR/sql/ and $SCRIPT_DIR/." >&2
  echo "Run this from a clone of the repo, or copy infra/scripts/sql/grant-roles.sql next to this script." >&2
  exit 1
fi
# The names go into an N'...' literal: double any single quote.
q() { printf '%s' "${1//\'/\'\'}"; }
$SQLCMD -S "$SQL_SERVER" -d "$SQL_DATABASE" --authentication-method ActiveDirectoryDefault -b \
  -v DeviceReadersGroup="$(q "${DEVICE_READERS_GROUP:-}")" \
     InvestigatorsGroup="$(q "${INVESTIGATORS_GROUP:-}")" \
     AuditReviewersGroup="$(q "${AUDIT_REVIEWERS_GROUP:-}")" \
  -i "$SQL_FILE"
