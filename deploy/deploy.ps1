#!/usr/bin/env pwsh
<#
.SYNOPSIS
Builds and deploys one version of OrderFlow to the production-like Compose stack.

.DESCRIPTION
Order of operations:
  1. Build the api, worker, migrator, and web images tagged with -Tag (default: the current Git commit).
  2. Start SQL Server, Redis, and Kafka and wait for their health checks.
  3. Run the migration bundle once. API containers never migrate, so replicas cannot race and a failed migration stops the release before any traffic moves.
  4. Replace api, worker, and web with the new tag and wait for their health checks.
  5. Check readiness through the API and liveness through the HTTPS edge, then record the tag in <env file>.releases.

Migrations must be backward compatible with the previous release (expand, then contract in a later release), because rollback.ps1
starts the previous images against the current schema.

.EXAMPLE
pwsh deploy/deploy.ps1
pwsh deploy/deploy.ps1 -Tag v1.0.0
pwsh deploy/deploy.ps1 -Tag 1a2b3c4 -SkipBuild -SkipMigrations   # what rollback.ps1 runs
#>
param(
    [string]$Tag,
    [string]$EnvFile = (Join-Path $PSScriptRoot '.env.prod'),
    [switch]$SkipBuild,
    [switch]$SkipMigrations
)

. (Join-Path $PSScriptRoot 'common.ps1')
$script:EnvFile = (Resolve-Path $EnvFile -ErrorAction SilentlyContinue)?.Path ?? $EnvFile
$values = Read-EnvFile $script:EnvFile
Set-Location $script:RepoRoot

if (-not $Tag) {
    $Tag = (git rev-parse --short HEAD).Trim()
    if (git status --porcelain --untracked-files=no -- src orderflow-web Dockerfile deploy) { $Tag = "$Tag-dirty" }
}
$env:IMAGE_TAG = $Tag
Write-Host "Deploying OrderFlow $Tag"

if (-not $SkipBuild) {
    Write-Host '== Building images =='
    Invoke-Compose --profile tools build migrator api worker web
}

Write-Host '== Starting SQL Server, Redis, and Kafka =='
Invoke-Compose up -d --wait --wait-timeout 180 sqlserver redis kafka

if (-not $SkipMigrations) {
    Write-Host '== Applying migrations =='
    Invoke-Compose --profile tools run --rm migrator
}

Write-Host '== Starting the application =='
# --no-deps leaves the data services untouched; Compose replaces a container only when its image or settings changed.
Invoke-Compose up -d --wait --wait-timeout 180 --no-build --no-deps api worker web

Write-Host '== Verifying =='
$readinessProbe = "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && cat <&3"
$ready = Wait-Until { (docker compose -f $script:ComposeFile --env-file $script:EnvFile exec -T api bash -c $readinessProbe) -join "`n" -match '"status":"Healthy"' } 90
if (-not $ready) { throw 'The API did not report every dependency Healthy. Check: docker compose -f deploy/compose.prod.yml --env-file deploy/.env.prod logs api' }
$site = Get-SiteUrl $values
$skipCertificate = $values['SITE_ADDRESS'] -in @($null, '', 'localhost')
$edge = Wait-Until { (Invoke-WebRequest "$site/health/live" -SkipCertificateCheck:$skipCertificate -SkipHttpErrorCheck -TimeoutSec 5).StatusCode -eq 200 } 60
if (-not $edge) { throw "$site/health/live did not answer through the edge." }
$workerState = (docker compose -f $script:ComposeFile --env-file $script:EnvFile ps worker --format '{{.State}}').Trim()
if ($workerState -ne 'running') { throw "The Worker is $workerState." }

"$((Get-Date).ToUniversalTime().ToString('o')) $Tag" | Add-Content (Get-ReleaseLog)
Write-Host "Deployed $Tag. The site is $site"
