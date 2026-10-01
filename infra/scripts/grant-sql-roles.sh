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
SQLCMD=${SQLCMD:-sqlcmd}
: "${SQL_SERVER:?set SQL_SERVER}"; : "${SQL_DATABASE:?set SQL_DATABASE}"
if [[ -z "${DEVICE_READERS_GROUP:-}${INVESTIGATORS_GROUP:-}${AUDIT_REVIEWERS_GROUP:-}" ]]; then
  echo "Set at least one of DEVICE_READERS_GROUP, INVESTIGATORS_GROUP, AUDIT_REVIEWERS_GROUP." >&2
  exit 2
fi
# The names go into an N'...' literal: double any single quote.
q() { printf '%s' "${1//\'/\'\'}"; }
$SQLCMD -S "$SQL_SERVER" -d "$SQL_DATABASE" --authentication-method ActiveDirectoryDefault -b \
  -v DeviceReadersGroup="$(q "${DEVICE_READERS_GROUP:-}")" \
     InvestigatorsGroup="$(q "${INVESTIGATORS_GROUP:-}")" \
     AuditReviewersGroup="$(q "${AUDIT_REVIEWERS_GROUP:-}")" \
  -i infra/scripts/sql/grant-roles.sql
