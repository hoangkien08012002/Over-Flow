#!/usr/bin/env pwsh
<#
.SYNOPSIS
Restores a backup made by backup.ps1.

.DESCRIPTION
With -TargetDatabase other than the live one, the backup is restored beside it, which is how a backup is tested without downtime.
Restoring over the live database stops the API and Worker first, because in-flight work would otherwise write into a database
that is about to be replaced. Events that were published after the backup was taken are redelivered or republished; consumers
deduplicate them through the Inbox, but side effects recorded only after the backup (for example a payment) are lost with it.

.EXAMPLE
pwsh deploy/restore.ps1 -BackupFile deploy/backups/OrderFlow_20261108T101500Z.bak -TargetDatabase OrderFlow_RestoreTest
pwsh deploy/restore.ps1 -BackupFile deploy/backups/OrderFlow_20261108T101500Z.bak -ReplaceLiveDatabase
#>
param(
    [Parameter(Mandatory)][string]$BackupFile,
    [string]$TargetDatabase,
    [switch]$ReplaceLiveDatabase,
    [string]$EnvFile = (Join-Path $PSScriptRoot '.env.prod')
)

. (Join-Path $PSScriptRoot 'common.ps1')
$script:EnvFile = $EnvFile
$values = Read-EnvFile $EnvFile
$live = $values['SQL_DATABASE'] ?? 'OrderFlow'
if (-not $TargetDatabase) { $TargetDatabase = $live }
if ($TargetDatabase -notmatch '^[A-Za-z0-9_]+$') { throw 'TargetDatabase may contain only letters, digits, and underscores.' }
if ($TargetDatabase -eq $live -and -not $ReplaceLiveDatabase) { throw "Restoring over the live database $live needs -ReplaceLiveDatabase." }
if (-not (Test-Path $BackupFile)) { throw "$BackupFile does not exist." }

$name = Split-Path $BackupFile -Leaf
$containerPath = "/var/opt/mssql/backups/$name"
Invoke-Compose exec -T sqlserver mkdir -p /var/opt/mssql/backups
Invoke-Compose cp $BackupFile "sqlserver:$containerPath"
# The backup was written by the mssql user inside the container; a copied-in file must be readable by it as well.
Invoke-Compose exec -T -u root sqlserver chown mssql "$containerPath"

# The logical file names come from the backup itself, so a restore works on a server that never hosted the source database.
# Each row starts with the logical name, the physical path, and the file type (D or L). SQL Server logical names have no spaces here.
$rows = @((Invoke-Sql "RESTORE FILELISTONLY FROM DISK = N'$containerPath'") -split "`n" | ForEach-Object { , ($_ -split '\s+') })
$data = @($rows | Where-Object { $_[2] -eq 'D' })[0][0]
$log = @($rows | Where-Object { $_[2] -eq 'L' })[0][0]
if (-not $data -or -not $log) { throw "Could not read the data and log file names from $name." }

if ($TargetDatabase -eq $live) {
    Write-Host 'Stopping the API and Worker before replacing the live database'
    Invoke-Compose stop api worker
}
Invoke-Sql "IF DB_ID(N'$TargetDatabase') IS NOT NULL ALTER DATABASE [$TargetDatabase] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [$TargetDatabase] FROM DISK = N'$containerPath' WITH REPLACE, CHECKSUM, MOVE N'$data' TO N'/var/opt/mssql/data/$TargetDatabase.mdf', MOVE N'$log' TO N'/var/opt/mssql/data/${TargetDatabase}_log.ldf'; ALTER DATABASE [$TargetDatabase] SET MULTI_USER;" | Out-Null
if ($TargetDatabase -eq $live) {
    Invoke-Compose start api worker
}
Write-Host "Restored $name into $TargetDatabase"
