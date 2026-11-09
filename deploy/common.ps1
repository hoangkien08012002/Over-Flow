# Shared helpers for the deployment scripts. Dot-source it; it is not meant to run on its own.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:ComposeFile = Join-Path $PSScriptRoot 'compose.prod.yml'

# Each environment file keeps its own release history beside it, so a rehearsal never changes what rollback.ps1 sees for production.
function Get-ReleaseLog { return "$script:EnvFile.releases" }

function Read-EnvFile([string]$path) {
    if (-not (Test-Path $path)) { throw "$path is missing. Copy deploy/.env.prod.example to it and set the secrets." }
    $values = @{}
    foreach ($line in Get-Content $path) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') { $values[$Matches[1]] = $Matches[2].Trim() }
    }
    $placeholders = @($values.Keys | Where-Object { $values[$_] -like 'REPLACE_*' })
    if ($placeholders) { throw "Replace the placeholder values in ${path}: $($placeholders -join ', ')" }
    if ($values['MSSQL_SA_PASSWORD'] -match "[;'`"]") { throw 'MSSQL_SA_PASSWORD must not contain ; or quotes, because it is placed inside a connection string.' }
    return $values
}

function Invoke-Compose {
    docker compose -f $script:ComposeFile --env-file $script:EnvFile @args
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($args -join ' ') failed with exit code $LASTEXITCODE." }
}

function Get-SiteUrl([hashtable]$values) {
    $site = if ($values['SITE_ADDRESS']) { $values['SITE_ADDRESS'] } else { 'localhost' }
    $port = if ($values['HTTPS_PORT']) { $values['HTTPS_PORT'] } else { '443' }
    return $(if ($port -eq '443') { "https://$site" } else { "https://${site}:$port" })
}

function Wait-Until([scriptblock]$condition, [int]$timeoutSeconds, [int]$intervalSeconds = 2) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Seconds $intervalSeconds
    }
    return $false
}

# Runs T-SQL inside the SQL Server container with the password already in its environment, so it never appears on a host command line.
function Invoke-Sql([string]$query, [string]$database = 'master') {
    $escaped = $query.Replace("'", "'\''")
    $output = docker compose -f $script:ComposeFile --env-file $script:EnvFile exec -T sqlserver bash -c "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P `"`$MSSQL_SA_PASSWORD`" -d $database -b -h -1 -W -Q 'SET NOCOUNT ON; $escaped'"
    if ($LASTEXITCODE -ne 0) { throw "SQL failed: $output" }
    return ($output | Where-Object { $_ -and $_.Trim() }) -join "`n"
}
