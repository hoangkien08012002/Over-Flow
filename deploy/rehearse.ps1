#!/usr/bin/env pwsh
<#
.SYNOPSIS
Rehearses a clean deployment, a backup and test restore, an upgrade, and a rollback on a throwaway stack.

.DESCRIPTION
Uses its own Compose project, ports, subnet, and generated secrets (deploy/.env.rehearsal, deleted afterward), so it never
touches a real deployment or needs deploy/.env.prod. Steps:
  1. Clean deploy of release A from empty volumes, then the smoke test (places order 1).
  2. Back up SQL Server, restore the backup beside the live database, and compare order counts.
  3. Deploy release B, then smoke test again (places order 2).
  4. Roll back to release A with rollback.ps1; check that A's images are running, both orders survived, and the stack still works.
Release A and B are built from the same source, so step 4 proves the mechanism (image tags, data kept, migrations not rerun),
not compatibility between two different code versions.

.EXAMPLE
pwsh deploy/rehearse.ps1
pwsh deploy/rehearse.ps1 -KeepStack
#>
param(
    [int]$HttpsPort = 8443,
    [int]$HttpPort = 8080,
    [switch]$KeepStack
)

. (Join-Path $PSScriptRoot 'common.ps1')
$script:EnvFile = Join-Path $PSScriptRoot '.env.rehearsal'
$secret = { param($bytes) [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes($bytes)).Replace('+', 'x').Replace('/', 'y').Replace('=', '') }
@"
COMPOSE_PROJECT_NAME=orderflow-rehearsal
MSSQL_SA_PASSWORD=Rh$(& $secret 18)9!
SQL_DATABASE=OrderFlow
JWT_SIGNING_KEY=$(& $secret 48)
SITE_ADDRESS=localhost
HTTP_BIND=127.0.0.1
HTTP_PORT=$HttpPort
HTTPS_PORT=$HttpsPort
BACKEND_SUBNET=172.31.250.0/24
EDGE_IP=172.31.250.10
IMAGE_REGISTRY=orderflow-rehearsal
"@ | Set-Content $script:EnvFile
Remove-Item (Get-ReleaseLog) -ErrorAction SilentlyContinue
$commit = (git -C $script:RepoRoot rev-parse --short HEAD).Trim()
$releaseA = "$commit-a"
$releaseB = "$commit-b"
$results = [System.Collections.Generic.List[string]]::new()
$exitCode = 1

try {
    Write-Host '== 1. Clean deployment of release A =='
    Invoke-Compose down -v --remove-orphans 2>$null
    & (Join-Path $PSScriptRoot 'deploy.ps1') -Tag $releaseA -EnvFile $script:EnvFile
    $order1 = & (Join-Path $PSScriptRoot 'smoke.ps1') -EnvFile $script:EnvFile | Select-Object -Last 1
    $results.Add("PASS  Clean deployment of $releaseA and smoke test (order $order1)")

    Write-Host '== 2. Backup and test restore =='
    $backup = & (Join-Path $PSScriptRoot 'backup.ps1') -EnvFile $script:EnvFile -OutputDirectory (Join-Path $PSScriptRoot 'backups/rehearsal') | Select-Object -Last 1
    & (Join-Path $PSScriptRoot 'restore.ps1') -EnvFile $script:EnvFile -BackupFile $backup -TargetDatabase OrderFlow_RestoreTest
    $liveCount = Invoke-Sql 'SELECT COUNT(*) FROM dbo.Orders' 'OrderFlow'
    $restoredCount = Invoke-Sql 'SELECT COUNT(*) FROM dbo.Orders' 'OrderFlow_RestoreTest'
    if ($liveCount -ne $restoredCount) { throw "The restored copy has $restoredCount orders; the live database has $liveCount." }
    Invoke-Sql 'DROP DATABASE [OrderFlow_RestoreTest]' | Out-Null
    $results.Add("PASS  Backup restored beside the live database with the same $liveCount order(s)")

    Write-Host '== 3. Upgrade to release B =='
    & (Join-Path $PSScriptRoot 'deploy.ps1') -Tag $releaseB -EnvFile $script:EnvFile
    $order2 = & (Join-Path $PSScriptRoot 'smoke.ps1') -EnvFile $script:EnvFile | Select-Object -Last 1
    $results.Add("PASS  Upgrade to $releaseB and smoke test (order $order2)")

    Write-Host '== 4. Roll back to release A =='
    & (Join-Path $PSScriptRoot 'rollback.ps1') -EnvFile $script:EnvFile
    $running = (docker compose -f $script:ComposeFile --env-file $script:EnvFile images api --format json | ConvertFrom-Json | Select-Object -First 1).Tag
    if ($running -ne $releaseA) { throw "After rollback the API runs $running, not $releaseA." }
    $kept = Invoke-Sql "SELECT COUNT(*) FROM dbo.Orders WHERE Id IN ('$order1', '$order2') AND Status = 'Processing'" 'OrderFlow'
    if ($kept -ne '2') { throw "Only $kept of the two smoke orders are still Processing after rollback." }
    & (Join-Path $PSScriptRoot 'smoke.ps1') -EnvFile $script:EnvFile | Out-Null
    $results.Add("PASS  Rollback to $releaseA kept both orders and passed the smoke test again")
    $exitCode = 0
}
catch {
    $results.Add("FAIL  $_")
    Write-Host "Logs: docker compose -f deploy/compose.prod.yml --env-file deploy/.env.rehearsal logs"
}
finally {
    if (-not $KeepStack -and $exitCode -eq 0) {
        Invoke-Compose down -v --remove-orphans 2>$null
        Remove-Item $script:EnvFile, (Get-ReleaseLog) -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $PSScriptRoot 'backups/rehearsal') -Recurse -ErrorAction SilentlyContinue
    }
}

Write-Host "`n== Rehearsal summary =="
$results | ForEach-Object { Write-Host $_ }
exit $exitCode
