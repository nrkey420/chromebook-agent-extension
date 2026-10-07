# Who can see what in Azure SQL

People get access through three database roles, each mapped to a Microsoft Entra group. Roles and permissions are
created by `collector/src/ChromeCollector.FunctionApp/Sql/004_security.sql`, which the deploy applies with the rest of
the schema. Adding groups to the roles is a one-time step a person runs (below).

| Role | For | Can | Cannot |
|---|---|---|---|
| `ChromebookDeviceReaders` | Helpdesk, ops, fleet/login dashboards | `vw_Devices`, `vw_Users`, `vw_LoginHistory`, `SyncState`, `IngestionErrors`; `usp_FindDevice`, `usp_UserDevices`, `usp_DeviceUsers` | See any URL, title, search or download; IP history; investigation timelines |
| `ChromebookInvestigators` | IR analysts | Everything device readers can, plus `vw_IpHistory` and every investigation procedure (`usp_WebActivity`, `usp_SiteVisitors`, `usp_IpLookup`, `usp_UserDevices`, `usp_DeviceUsers`, `usp_DeviceTimeline`, `usp_UserTimeline`, `usp_WhoWasOnIp`, `usp_FindDevice`) | Read web content directly (`ActivityEvents`, `vw_WebActivity`, `vw_SearchActivity`, `vw_Downloads`, `vw_InvestigationTimeline`); read or change the audit trail |
| `ChromebookAuditReviewers` | IR lead, compliance | Read `InvestigationAudit` | Change it; anything else |

All three can use `dbo.fn_ToLocal` / `dbo.fn_ToUtc` in their own queries.

**How it holds together:** web content is only reachable through the investigation procedures, and every procedure
run is written to `InvestigationAudit` (who, when, case number, filters, rows). The procedures can read the tables
because they and the tables share an owner (ownership chaining), not because the caller has rights on the tables.
Explicit DENYs on web content and on changing the audit trail keep these closed even if someone is later added to a
broad role such as `db_datareader`. Tests check every allowed and denied action for each role.

**What it does not restrict:** the SQL Entra admin group (database owner) and the collector's managed identity. Keep
the admin group to the people who administer the database; never put analysts or helpdesk staff in it.

## One-time setup

1. In Microsoft Entra ID create three security groups, for example `SQL-Chromebook-DeviceReaders`,
   `SQL-Chromebook-Investigators`, `SQL-Chromebook-AuditReviewers`, and add people to them. From then on, access is
   managed only through group membership.
2. Make sure the schema (including `004_security.sql`) has been applied: a deploy from `main` does this, or run
   `infra/scripts/init-sql.sh`.
3. As a person who is the SQL Entra admin (your IP allowed by the SQL firewall, `az login` done, go-sqlcmd installed;
   from Azure Cloud Shell see [below](#running-the-sql-scripts-from-azure-cloud-shell)):

   ```bash
   SQL_SERVER=<sqlServerFqdn> SQL_DATABASE=<sqlDatabaseName> \
   DEVICE_READERS_GROUP='SQL-Chromebook-DeviceReaders' \
   INVESTIGATORS_GROUP='SQL-Chromebook-Investigators' \
   AUDIT_REVIEWERS_GROUP='SQL-Chromebook-AuditReviewers' \
     bash infra/scripts/grant-sql-roles.sh
   ```

   PowerShell: `infra/scripts/grant-sql-roles.ps1 -SqlServer … -SqlDatabase … -DeviceReadersGroup … -InvestigatorsGroup … -AuditReviewersGroup …`.
   Any group can be left out and added later; re-running is safe. The script prints the current membership.

   It must be run by a person: creating a database user for an Entra group looks the name up in Entra ID, which a
   service principal (like the deploy pipeline) can only do if the SQL server has the Directory Readers role.

People then connect to the database with their own Entra account (SSMS, Azure Data Studio, VS Code `mssql`).

## Running the SQL scripts from Azure Cloud Shell

Cloud Shell has the Azure CLI and is already signed in, but it does not include `sqlcmd`, and it connects to SQL from its
own public IP:

```bash
# 0. Get the scripts (the repo is public; your home folder is kept between sessions)
git clone https://github.com/nrkey420/chromebook-agent-extension.git ~/chromebook-agent-extension
cd ~/chromebook-agent-extension            # later: git pull to update

# 1. Install go-sqlcmd into ~/bin (kept between Cloud Shell sessions)
bash infra/scripts/install-sqlcmd.sh
export PATH="$HOME/bin:$PATH"            # add this line to ~/.bashrc to keep it

# 2. Let this Cloud Shell session through the SQL firewall (its IP changes between sessions)
MYIP=$(curl -s https://api.ipify.org)
az sql server firewall-rule create -g <resource-group> -s <sqlServerName> -n cloudshell \
  --start-ip-address "$MYIP" --end-ip-address "$MYIP" -o none

# 3. Run the script (it signs in with your az login)
SQL_SERVER=<sqlServerFqdn> SQL_DATABASE=<sqlDatabaseName> INVESTIGATORS_GROUP='...' bash infra/scripts/grant-sql-roles.sh

# 4. Remove the firewall rule when you are done
az sql server firewall-rule delete -g <resource-group> -s <sqlServerName> -n cloudshell
```

The scripts find their `.sql` files relative to their own location, so they can be run from any folder, but
they need the rest of the repo next to them: clone it rather than copying single scripts. The same applies to `init-sql.sh` and to `run-google-sync.sh` when it waits for results. `<sqlServerName>` is the
short server name (deployment output `sqlServerName`); `SQL_SERVER` is the full name (`sqlServerFqdn`).

## Checking access

```sql
-- Members of each Chromebook role
SELECT r.name AS RoleName, m.name AS Member, m.type_desc
FROM sys.database_role_members rm
JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
WHERE r.name LIKE 'Chromebook%' ORDER BY RoleName, Member;

-- Who is in db_owner / db_datareader (should be the admin group and the collector identity only)
SELECT r.name AS RoleName, m.name AS Member
FROM sys.database_role_members rm
JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
WHERE r.name IN ('db_owner', 'db_datareader', 'db_datawriter');
```

## Not covered yet

- **Per-school access** (a principal sees only their school): needs row-level security keyed on OU, plus a mapping
  of staff to schools. Planned in `docs/plans/reporting-and-dashboards.md`, section 5.
- **Power BI web-activity page:** no role gets bulk, unaudited read of web content, so that page needs either
  aggregated rollup tables (domains and counts, no per-user URLs) or a service identity with its own access review.
  Fleet and login pages work with `ChromebookDeviceReaders`.
- **Azure SQL auditing** to Log Analytics (server-level record of every query, including the admin group's): turn it
  on in the portal (SQL server → Auditing) or add it to Bicep.
