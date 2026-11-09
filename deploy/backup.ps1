#!/usr/bin/env pwsh
<#
.SYNOPSIS
Takes a full, checksummed SQL Server backup and copies it out of the container to deploy/backups/.

.DESCRIPTION
SQL Server holds every authoritative record (orders, inventory, payments, outbox, inbox), so it is what gets backed up.
Redis is a cache and is rebuilt from SQL. Kafka events that were committed to the outbox are republished, and consumers
deduplicate through the Inbox, so a restore never needs the Kafka log. Keep copies off this host for real disaster recovery.

.EXAMPLE
pwsh deploy/backup.ps1
#>
param(
    [string]$EnvFile = (Join-Path $PSScriptRoot '.env.prod'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'backups')
)

. (Join-Path $PSScriptRoot 'common.ps1')
$script:EnvFile = $EnvFile
$values = Read-EnvFile $EnvFile
$database = $values['SQL_DATABASE'] ?? 'OrderFlow'
$name = "${database}_$((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')).bak"
$containerPath = "/var/opt/mssql/backups/$name"
Invoke-Compose exec -T sqlserver mkdir -p /var/opt/mssql/backups

Invoke-Sql "BACKUP DATABASE [$database] TO DISK = N'$containerPath' WITH INIT, CHECKSUM; RESTORE VERIFYONLY FROM DISK = N'$containerPath' WITH CHECKSUM;" | Out-Null
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
Invoke-Compose cp "sqlserver:$containerPath" (Join-Path $OutputDirectory $name)
Write-Host "Backup verified and saved to $(Join-Path $OutputDirectory $name)"
return (Join-Path $OutputDirectory $name)
