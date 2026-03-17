#!/usr/bin/env bash
set -euo pipefail
SQLCMD=${SQLCMD:-sqlcmd}
: "${SQL_SERVER:?set SQL_SERVER}"; : "${SQL_DATABASE:?set SQL_DATABASE}"; : "${SQL_USER:?set SQL_USER}"; : "${SQL_PASSWORD:?set SQL_PASSWORD}"
for f in collector/src/ChromeCollector.FunctionApp/Sql/001_create_tables.sql collector/src/ChromeCollector.FunctionApp/Sql/002_create_indexes.sql collector/src/ChromeCollector.FunctionApp/Sql/003_create_views.sql collector/src/ChromeCollector.FunctionApp/Sql/004_seed_reference_data.sql; do
  $SQLCMD -S "$SQL_SERVER" -d "$SQL_DATABASE" -U "$SQL_USER" -P "$SQL_PASSWORD" -i "$f"
done
