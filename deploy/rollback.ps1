#!/usr/bin/env pwsh
<#
.SYNOPSIS
Puts the previous application version back without touching the schema or data.

.DESCRIPTION
Reads the release history beside the environment file (deploy/.env.prod.releases), picks the newest tag that differs from the current one (or -Tag), and redeploys those images with
-SkipBuild -SkipMigrations. Schema changes are not reversed: releases keep migrations backward compatible so the previous
version runs on the current schema. Restoring a backup (restore.ps1) is the separate, last-resort path for a bad data change.

.EXAMPLE
pwsh deploy/rollback.ps1
pwsh deploy/rollback.ps1 -Tag v1.0.0
#>
param(
    [string]$Tag,
    [string]$EnvFile = (Join-Path $PSScriptRoot '.env.prod')
)

. (Join-Path $PSScriptRoot 'common.ps1')
$script:EnvFile = $EnvFile
$registry = (Read-EnvFile $EnvFile)['IMAGE_REGISTRY'] ?? 'orderflow'
if (-not $Tag) {
    if (-not (Test-Path (Get-ReleaseLog))) { throw "$(Get-ReleaseLog) does not exist, so there is no previous release to return to." }
    $tags = @(Get-Content (Get-ReleaseLog) | ForEach-Object { ($_ -split ' ', 2)[1] })
    $current = $tags[-1]
    $Tag = @($tags | Where-Object { $_ -ne $current })[-1]
    if (-not $Tag) { throw "Every recorded release is $current; there is nothing to roll back to." }
    Write-Host "Rolling back from $current to $Tag"
}

foreach ($image in 'api', 'worker', 'web') {
    docker image inspect "${registry}/${image}:$Tag" *> $null
    if ($LASTEXITCODE -ne 0) { throw "Image ${registry}/${image}:$Tag is not on this host. Pull it from the registry first." }
}
& (Join-Path $PSScriptRoot 'deploy.ps1') -Tag $Tag -EnvFile $EnvFile -SkipBuild -SkipMigrations
