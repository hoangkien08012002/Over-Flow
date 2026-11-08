#!/usr/bin/env pwsh
<#
.SYNOPSIS
Runs the customer and staff journeys in a real browser against the production web build, the real API and Worker, SQL Server, Redis, and Kafka.

.DESCRIPTION
Uses an isolated database and topic prefix and removes them afterward. The API runs in Development because the production cookie is
Secure and this run serves plain HTTP. The Playwright suite starts the Worker itself, partway through, to prove that the page follows an order that was waiting.
Requires Docker, .env, the EF Core 10 tool, npm, and a browser (Edge by default; set E2E_BROWSER_CHANNEL to change it).

.EXAMPLE
pwsh tests/e2e/run-browser-e2e.ps1
#>
param(
    [string]$EfTool = 'dotnet-ef',
    [string]$LogDir = (Join-Path ([IO.Path]::GetTempPath()) "orderflow-browser-e2e-$(Get-Date -Format yyyyMMddHHmmss)"),
    [int]$ApiPort = 5198,
    [int]$WebPort = 4173,
    [switch]$KeepDatabase
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $root
New-Item -ItemType Directory -Force $LogDir | Out-Null

$password = (Get-Content .env | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' } | Select-Object -First 1).Split('=', 2)[1]
if (-not $password) { throw 'MSSQL_SA_PASSWORD is missing from .env' }
$runId = Get-Date -Format yyyyMMddHHmmss
$database = "OrderFlow_Browser_$runId"
$api = "http://127.0.0.1:$ApiPort"
$processes = @()
$exitCode = 1

function Compose { docker compose --env-file .env @args }
function Wait-Until([scriptblock]$condition, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) { try { if (& $condition) { return $true } } catch { }; Start-Sleep -Seconds 1 }
    return $false
}
function Grant-Roles([string]$email, [string]$roles) {
    Compose exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -d $database -b -Q "INSERT dbo.AspNetUserRoles (UserId, RoleId) SELECT u.Id, r.Id FROM dbo.AspNetUsers u CROSS JOIN dbo.AspNetRoles r WHERE u.Email = '$email' AND r.Name IN ($roles)" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Granting roles to $email failed." }
}
function New-Staff([string]$prefix, [string]$roles) {
    $email = "$prefix-$runId@example.invalid"
    Invoke-RestMethod -Method Post -Uri "$api/api/v1/auth/register" -ContentType 'application/json' -Body (@{ email = $email; password = 'StrongPassword123!'; firstName = $prefix; lastName = 'Staff' } | ConvertTo-Json) | Out-Null
    Grant-Roles $email $roles
    return $email
}

try {
    Write-Host "Logs: $LogDir"
    Write-Host '== Infrastructure and builds =='
    Compose up -d sqlserver redis kafka 2>&1 | Out-Null
    if (-not (Wait-Until { Compose exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -Q 'SELECT 1' 2>&1 | Out-Null; $LASTEXITCODE -eq 0 } 120)) { throw 'SQL Server did not become ready.' }
    dotnet build OrderFlow.sln --nologo -v quiet 2>&1 | Out-File (Join-Path $LogDir 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; see build.log' }

    $connection = "Server=127.0.0.1,1433;Database=$database;User ID=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
    $env:ORDERFLOW_SQL_CONNECTION = $connection
    $env:ConnectionStrings__OrderFlow = $connection
    & $EfTool database update --project src/OrderFlow.Infrastructure --startup-project src/OrderFlow.Infrastructure --no-build --connection $connection 2>&1 | Out-File (Join-Path $LogDir 'migrate.log')
    if ($LASTEXITCODE -ne 0) { throw 'Migration failed; see migrate.log' }

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $api
    $env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $env:Kafka__TopicPrefix = "browser$runId."
    $env:Kafka__ConsumerGroupPrefix = "browser$runId."
    # Every page load refreshes the session, and one browser run comes from one address, so the per-address limits are lifted here.
    foreach ($limit in 'Register', 'Login', 'Refresh', 'Logout', 'Search', 'Checkout') { Set-Item "Env:RateLimits__$limit" '5000' }

    $apiDirectory = Join-Path $root 'src/OrderFlow.Api/bin/Debug/net10.0'
    $processes += Start-Process dotnet -ArgumentList (Join-Path $apiDirectory 'OrderFlow.Api.dll') -WorkingDirectory $apiDirectory -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $LogDir 'api.log') -RedirectStandardError (Join-Path $LogDir 'api.err.log')
    if (-not (Wait-Until { (Invoke-RestMethod "$api/api/v1/shipping-methods").Count -ge 0 } 60)) { throw 'API did not start; see api.log' }

    Write-Host '== Staff accounts =='
    $env:E2E_ADMIN_EMAIL = New-Staff 'admin' "'Admin'"
    $env:E2E_INVENTORY_EMAIL = New-Staff 'inventory' "'InventoryManager'"
    $env:E2E_SUPPORT_EMAIL = New-Staff 'support' "'Support'"

    Write-Host '== Web client =='
    $env:VITE_API_PROXY_TARGET = $api
    # npm ci deletes node_modules first, which fails while a dev server holds its files, so it runs only on a fresh checkout.
    if (-not (Test-Path orderflow-web/node_modules/.package-lock.json)) {
        npm --prefix orderflow-web ci 2>&1 | Out-File (Join-Path $LogDir 'npm-ci.log')
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed; see npm-ci.log' }
    }
    npm --prefix orderflow-web run build 2>&1 | Out-File (Join-Path $LogDir 'web-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'The web build failed; see web-build.log' }
    $vite = Join-Path $root 'orderflow-web/node_modules/vite/bin/vite.js'
    $processes += Start-Process node -ArgumentList $vite, 'preview', '--host', '127.0.0.1', '--port', $WebPort, '--strictPort' -WorkingDirectory (Join-Path $root 'orderflow-web') -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $LogDir 'web.log') -RedirectStandardError (Join-Path $LogDir 'web.err.log')
    if (-not (Wait-Until { (Invoke-WebRequest "http://127.0.0.1:$WebPort/" -SkipHttpErrorCheck).StatusCode -eq 200 } 30)) { throw 'The web client did not start; see web.log' }

    Write-Host '== Browser journeys =='
    $env:E2E_BASE_URL = "http://127.0.0.1:$WebPort"
    $env:E2E_WORKER_DLL = Join-Path $root 'src/OrderFlow.Worker/bin/Debug/net10.0/OrderFlow.Worker.dll'
    $env:E2E_LOG_DIR = $LogDir
    $env:E2E_OUTPUT_DIR = Join-Path $LogDir 'playwright'
    Push-Location orderflow-web
    npx playwright test 2>&1 | Tee-Object (Join-Path $LogDir 'playwright.log')
    $exitCode = $LASTEXITCODE
    Pop-Location
}
catch {
    Write-Host "HARNESS FAILURE: $_"
    $exitCode = 1
}
finally {
    foreach ($process in $processes) { if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
    if (-not $KeepDatabase) { Compose exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -Q "IF DB_ID('$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database] END" 2>&1 | Out-Null }
}

Write-Host "Browser E2E exit code: $exitCode. Logs: $LogDir"
exit $exitCode
