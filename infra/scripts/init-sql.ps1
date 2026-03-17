param([string]$SqlServer,[string]$SqlDatabase,[string]$SqlUser,[string]$SqlPassword)
$files=@('collector/src/ChromeCollector.FunctionApp/Sql/001_create_tables.sql','collector/src/ChromeCollector.FunctionApp/Sql/002_create_indexes.sql','collector/src/ChromeCollector.FunctionApp/Sql/003_create_views.sql','collector/src/ChromeCollector.FunctionApp/Sql/004_seed_reference_data.sql')
foreach($f in $files){ sqlcmd -S $SqlServer -d $SqlDatabase -U $SqlUser -P $SqlPassword -i $f }
