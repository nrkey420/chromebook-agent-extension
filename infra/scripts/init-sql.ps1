param([string]$SqlServer,[string]$SqlDatabase,[string]$SqlUser,[string]$SqlPassword)
$files=@('collector/src/ChromeCollector.FunctionApp/Sql/001_tables.sql','collector/src/ChromeCollector.FunctionApp/Sql/002_views.sql','collector/src/ChromeCollector.FunctionApp/Sql/003_procedures.sql')
foreach($f in $files){ sqlcmd -S $SqlServer -d $SqlDatabase -U $SqlUser -P $SqlPassword -b -i $f; if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed on $f" } }
